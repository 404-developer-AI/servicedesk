using Servicedesk.Api.Tests.TestInfrastructure;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.58 — trigger actions that take their value from a form field. A
/// trigger runs as the system, so the owner picked on a form must still be an
/// active agent with access to the ticket's queue, and a pending-till from a
/// form only lands on a ticket that is in a Pending status.
[Collection(PostgresCollection.Name)]
public sealed class FormFieldActionsDbTests
{
    private readonly PostgresDatabase _db;

    public FormFieldActionsDbTests(PostgresDatabase db) => _db = db;

    private static TriggerChangeSet SavePass(Guid? technician, DateTime? plannedUtc) => TriggerChangeSet.ForForm(
        new TriggerFormEvent(string.Empty, Guid.NewGuid(), null, "Plan intervention", null, null, null, null, string.Empty, null)
        {
            SaveEvent = FormSavedEventKind.Created,
            Fields = new Dictionary<string, TriggerFormField>(StringComparer.OrdinalIgnoreCase)
            {
                ["technician"] = new(FormFieldType.Agent, "Technician", technician is null ? string.Empty : "Sam Example", technician, null),
                ["planned_at"] = new(FormFieldType.DateTime, "Planned at", plannedUtc is null ? string.Empty : "planned", null, plannedUtc),
            },
        });

    private async Task GrantAsync(Guid user, Guid queue)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("INSERT INTO user_queue_access (user_id, queue_id) VALUES (@user, @queue)", new { user, queue });
    }

    private async Task<TriggerActionResult> ApplyOwnerAsync(Guid ticketId, TriggerChangeSet cs)
    {
        var tickets = new TicketRepository(_db.DataSource);
        var handler = new SetOwnerHandler(
            TriggerWriteKit.Create(_db.DataSource, NoSla()), new UserService(_db.DataSource),
            new QueueAccessService(_db.DataSource, new MemoryCache(new MemoryCacheOptions())), tickets);
        var ticket = (await tickets.GetCoreAsync(ticketId, default))!;
        using var action = JsonDocument.Parse("""{ "kind": "set_owner", "form_field": "technician" }""");
        return await handler.ApplyAsync(action.RootElement,
            new TriggerEvaluationContext(ticketId, ticket, null, cs, DateTime.UtcNow, Guid.NewGuid()), default);
    }

    private async Task<Guid?> OwnerAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid?>("SELECT assignee_user_id FROM tickets WHERE id = @ticketId", new { ticketId });
    }

    [PostgresFact]
    public async Task A_technician_with_queue_access_becomes_the_owner()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Printer down", await seed.ContactAsync(), q, s, p);
        var technician = await seed.UserAsync("Agent");
        await GrantAsync(technician, q);

        var result = await ApplyOwnerAsync(ticketId, SavePass(technician, null));

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        Assert.Equal(technician, await OwnerAsync(ticketId));
    }

    [PostgresFact]
    public async Task A_technician_without_queue_access_is_refused_and_the_owner_stays()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Printer down", await seed.ContactAsync(), q, s, p);
        var technician = await seed.UserAsync("Agent"); // no access to q

        var result = await ApplyOwnerAsync(ticketId, SavePass(technician, null));

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Null(await OwnerAsync(ticketId));
    }

    [PostgresFact]
    public async Task An_empty_technician_field_changes_nothing()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Printer down", await seed.ContactAsync(), q, s, p);

        Assert.Equal(TriggerActionStatus.NoOp, (await ApplyOwnerAsync(ticketId, SavePass(null, null))).Status);
    }

    private async Task<TriggerActionResult> ApplyPendingAsync(Guid ticketId, TriggerChangeSet cs)
    {
        var tickets = new TicketRepository(_db.DataSource);
        var handler = new SetPendingTillHandler(
            new SystemFieldMutator(_db.DataSource), null!, tickets, new TaxonomyRepository(_db.DataSource));
        var ticket = (await tickets.GetCoreAsync(ticketId, default))!;
        using var action = JsonDocument.Parse("""{ "kind": "set_pending_till", "form_field": "planned_at" }""");
        return await handler.ApplyAsync(action.RootElement,
            new TriggerEvaluationContext(ticketId, ticket, null, cs, DateTime.UtcNow, Guid.NewGuid()), default);
    }

    private async Task<DateTime?> PendingTillAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<DateTime?>("SELECT pending_till_utc FROM tickets WHERE id = @ticketId", new { ticketId });
    }

    [PostgresFact]
    public async Task The_planned_date_becomes_the_pending_till_on_a_pending_ticket()
    {
        var seed = new DbSeed(_db.DataSource);
        var q = await seed.QueueAsync();
        var pending = await seed.StatusAsync("Pending");
        var ticketId = await seed.TicketAsync("Printer down", await seed.ContactAsync(), q, pending, await seed.PriorityAsync());
        var planned = DateTime.UtcNow.AddDays(3);

        var result = await ApplyPendingAsync(ticketId, SavePass(null, planned));

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        var stored = await PendingTillAsync(ticketId);
        Assert.NotNull(stored);
        Assert.True((DateTime.SpecifyKind(stored!.Value, DateTimeKind.Utc) - planned).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [PostgresFact]
    public async Task A_planned_date_on_a_ticket_that_is_not_pending_is_refused()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, open, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Printer down", await seed.ContactAsync(), q, open, p);

        var result = await ApplyPendingAsync(ticketId, SavePass(null, DateTime.UtcNow.AddDays(3)));

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Null(await PendingTillAsync(ticketId));
    }

    [PostgresFact]
    public async Task A_planned_date_in_the_past_is_refused()
    {
        var seed = new DbSeed(_db.DataSource);
        var q = await seed.QueueAsync();
        var pending = await seed.StatusAsync("Pending");
        var ticketId = await seed.TicketAsync("Printer down", await seed.ContactAsync(), q, pending, await seed.PriorityAsync());

        var result = await ApplyPendingAsync(ticketId, SavePass(null, DateTime.UtcNow.AddHours(-1)));

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Null(await PendingTillAsync(ticketId));
    }

    private static ISlaEngine NoSla() => Fake<ISlaEngine>.Create((method, _) =>
        method.ReturnType == typeof(Task)
            ? Task.CompletedTask
            : throw new NotImplementedException($"Unexpected call: {method.Name}"));
}
