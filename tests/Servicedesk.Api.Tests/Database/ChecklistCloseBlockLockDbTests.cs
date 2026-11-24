using System.Text.Json;
using Dapper;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The checklist close block under the ticket row lock (ROADMAP §2 follow-up
/// (c)): TicketFieldWriter re-checks it for the status it will actually set —
/// also a queue auto-flip into a closing status, which the rules' precheck
/// cannot see — and a checklist write cannot commit while the status change
/// holds the row (it updates the ticket's counters), so an item reopened at
/// the same moment lands before or after it, never in between. Real
/// checklist rows, real guard, real writer.
[Collection(PostgresCollection.Name)]
public sealed class ChecklistCloseBlockLockDbTests
{
    private readonly PostgresDatabase _db;

    public ChecklistCloseBlockLockDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task The_writer_refuses_a_close_while_a_blocking_checklist_has_open_items()
    {
        var w = await WorldAsync();

        var result = await Writer().ApplyAsync(w.TicketId, new TicketFieldUpdate(StatusId: w.Closed), Agent(w), default);

        Assert.Equal(TicketFieldWriteStatus.ChecklistIncomplete, result.Status);
        Assert.Equal(w.Closed, result.BlockedStatusId);
        Assert.Equal("Onboarding", Assert.Single(result.Blockers!).Name);
        Assert.Equal(w.Open, await StatusAsync(w.TicketId));
    }

    [PostgresFact]
    public async Task A_queue_move_that_auto_flips_into_a_closing_status_is_refused_too()
    {
        var w = await WorldAsync();
        var closingQueue = await QueueWithDefaultAsync(w.Closed);

        var result = await Writer().ApplyAsync(w.TicketId, new TicketFieldUpdate(QueueId: closingQueue), Agent(w), default);

        Assert.Equal(TicketFieldWriteStatus.ChecklistIncomplete, result.Status);
        Assert.Equal((w.Queue, w.Open), await QueueAndStatusAsync(w.TicketId));
    }

    [PostgresFact]
    public async Task A_finished_checklist_lets_the_close_through()
    {
        var w = await WorldAsync();
        await new TicketChecklistRepository(_db.DataSource).SetItemStateAsync(w.ItemId, ChecklistItemState.Done, "", "", w.UserId, default);

        var result = await Writer().ApplyAsync(w.TicketId, new TicketFieldUpdate(StatusId: w.Closed), Agent(w), default);

        Assert.Equal(TicketFieldWriteStatus.Applied, result.Status);
        Assert.Equal(w.Closed, await StatusAsync(w.TicketId));
    }

    [PostgresFact]
    public async Task A_trigger_queue_move_into_a_closing_status_is_a_reported_no_op()
    {
        var w = await WorldAsync();
        var closingQueue = await QueueWithDefaultAsync(w.Closed);
        var reported = new List<string>();
        var reporter = Fake<IChecklistCloseBlockReporter>.Create((m, _) => { reported.Add(m.Name); return Task.CompletedTask; });
        var handler = new SetQueueHandler(
            TriggerWriteKit.Create(_db.DataSource, TriggerWriteKit.Sla(), writerGuard: Guard(), reporter: reporter),
            new TaxonomyRepository(_db.DataSource));
        var ticket = (await TriggerWriteKit.Tickets(_db.DataSource).GetCoreAsync(w.TicketId, default))!;
        using var action = JsonDocument.Parse($$"""{ "queue_id": "{{closingQueue}}" }""");

        var result = await handler.ApplyAsync(action.RootElement,
            new TriggerEvaluationContext(w.TicketId, ticket, null, TriggerChangeSet.Empty, DateTime.UtcNow, Guid.NewGuid()), default);

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Single(reported);
        Assert.Equal((w.Queue, w.Open), await QueueAndStatusAsync(w.TicketId));
    }

    [PostgresFact]
    public async Task A_checklist_write_waits_while_a_status_change_holds_the_ticket_row()
    {
        var w = await WorldAsync();
        var checklists = new TicketChecklistRepository(_db.DataSource);
        await checklists.SetItemStateAsync(w.ItemId, ChecklistItemState.Done, "", "", w.UserId, default);

        // The writer's lock, held open.
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SELECT 1 FROM tickets WHERE id = @id FOR UPDATE", new { id = w.TicketId }, tx);

        var reopen = checklists.SetItemStateAsync(w.ItemId, ChecklistItemState.Open, "", "", w.UserId, default);
        var raced = await Task.WhenAny(reopen, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(reopen, raced);

        await tx.RollbackAsync();
        Assert.True((await reopen.WaitAsync(TimeSpan.FromSeconds(10)))!.Changed);
    }

    // ---- plumbing -----------------------------------------------------------

    private sealed record World(Guid TicketId, Guid Queue, Guid Open, Guid Closed, Guid ItemId, Guid UserId);

    private IChecklistCloseGuard Guard() => new ChecklistCloseGuard(
        new TicketChecklistRepository(_db.DataSource),
        new TaxonomyRepository(_db.DataSource),
        new ChecklistSettingsReader(new InMemorySettingsService()));

    private TicketFieldWriter Writer() => TriggerWriteKit.Writer(_db.DataSource, Guard());

    private static TicketWriteOrigin Agent(World w) => new TicketWriteOrigin.Agent(w.UserId);

    /// A ticket in an Open status with one blocking checklist holding one
    /// required open item.
    private async Task<World> WorldAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var queue = await seed.QueueAsync();
        var open = await seed.StatusAsync("Open");
        var closed = await seed.StatusAsync("Closed");
        var ticketId = await seed.TicketAsync("Onboard new hire", await seed.ContactAsync(), queue, open, await seed.PriorityAsync());
        var userId = await seed.UserAsync();
        var checklists = new TicketChecklistRepository(_db.DataSource);
        var definition = new ChecklistTemplateDefinition
        {
            Sections = { new ChecklistTemplateSection { Items = { new ChecklistTemplateItem { Title = "Create account", IsRequired = true } } } },
        };
        await checklists.AttachAsync(ticketId, null, "Onboarding", "", blockClose: true, definition, userId, default);
        var itemId = (await checklists.ListForTicketAsync(ticketId, default)).Single().Items.Single().Id;
        return new World(ticketId, queue, open, closed, itemId, userId);
    }

    private async Task<Guid> QueueWithDefaultAsync(Guid defaultStatus)
    {
        var queue = await new DbSeed(_db.DataSource).QueueAsync("Done queue");
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE queues SET allowed_status_ids = ARRAY[@defaultStatus]::uuid[], default_status_id = @defaultStatus WHERE id = @queue",
            new { queue, defaultStatus });
        return queue;
    }

    private async Task<Guid> StatusAsync(Guid ticketId) => (await QueueAndStatusAsync(ticketId)).Status;

    private async Task<(Guid Queue, Guid Status)> QueueAndStatusAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.QuerySingleAsync<(Guid, Guid)>(
            "SELECT queue_id, status_id FROM tickets WHERE id = @ticketId", new { ticketId });
    }
}
