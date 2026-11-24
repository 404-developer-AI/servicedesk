using System.Text.Json;
using Dapper;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The trigger "Set queue" action follows the agent path's queue-change
/// rule: a status outside the new queue's allowed list flips to that queue's
/// default status (TicketRepository.UpdateFieldsAsync does the same for
/// agent writes).
[Collection(PostgresCollection.Name)]
public sealed class SetQueueAutoFlipDbTests
{
    private readonly PostgresDatabase _db;

    public SetQueueAutoFlipDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Status_outside_the_new_queue_flips_to_its_default()
    {
        var seed = new DbSeed(_db.DataSource);
        var (from, oldStatus, priority) = await seed.TaxonomyAsync();
        var allowed = await seed.StatusAsync();
        var to = await ScopedQueueAsync(seed, allowed);
        var ticketId = await seed.TicketAsync("Move me", await seed.ContactAsync(), from, oldStatus, priority);

        var result = await ApplyAsync(ticketId, to);

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        var (queue, status) = await ReadAsync(ticketId);
        Assert.Equal(to, queue);
        Assert.Equal(allowed, status);
        await using var c = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ticket_events WHERE ticket_id = @ticketId AND event_type = 'StatusChange'",
            new { ticketId }));
    }

    [PostgresFact]
    public async Task Status_already_allowed_in_the_new_queue_stays()
    {
        var seed = new DbSeed(_db.DataSource);
        var (from, _, priority) = await seed.TaxonomyAsync();
        var allowed = await seed.StatusAsync();
        var to = await ScopedQueueAsync(seed, allowed);
        var ticketId = await seed.TicketAsync("Move me", await seed.ContactAsync(), from, allowed, priority);

        await ApplyAsync(ticketId, to);

        var (queue, status) = await ReadAsync(ticketId);
        Assert.Equal(to, queue);
        Assert.Equal(allowed, status);
    }

    private async Task<Guid> ScopedQueueAsync(DbSeed seed, Guid allowedStatus)
    {
        var queue = await seed.QueueAsync("Scoped");
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE queues SET allowed_status_ids = ARRAY[@allowedStatus]::uuid[], default_status_id = @allowedStatus WHERE id = @queue",
            new { allowedStatus, queue });
        return queue;
    }

    private async Task<TriggerActionResult> ApplyAsync(Guid ticketId, Guid queueId)
    {
        var tickets = TriggerWriteKit.Tickets(_db.DataSource);
        var sla = Fake<ISlaEngine>.Create((method, _) =>
            method.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : throw new NotImplementedException($"Unexpected call: {method.Name}"));
        var handler = new SetQueueHandler(TriggerWriteKit.Create(_db.DataSource, sla), new TaxonomyRepository(_db.DataSource));

        var ticket = (await tickets.GetCoreAsync(ticketId, default))!;
        var ctx = new TriggerEvaluationContext(
            ticketId, ticket, null, TriggerChangeSet.Empty, DateTime.UtcNow, Guid.NewGuid());
        using var action = JsonDocument.Parse($$"""{ "kind": "set_queue", "queue_id": "{{queueId}}" }""");
        return await handler.ApplyAsync(action.RootElement, ctx, default);
    }

    private async Task<(Guid Queue, Guid Status)> ReadAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.QuerySingleAsync<(Guid, Guid)>(
            "SELECT queue_id, status_id FROM tickets WHERE id = @ticketId", new { ticketId });
    }
}
