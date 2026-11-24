using System.Text.Json;
using Dapper;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The trigger <c>set_pending_till</c> action with a fixed value (absolute /
/// relative / business days) keeps the Pending invariant like the form-field
/// mode: on a ticket outside a Pending status it fails and writes nothing.
[Collection(PostgresCollection.Name)]
public sealed class SetPendingTillStatusDbTests
{
    private readonly PostgresDatabase _db;

    public SetPendingTillStatusDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task A_fixed_value_on_a_ticket_that_is_not_pending_fails_and_writes_nothing()
    {
        var ticketId = await TicketAsync("Open");

        var result = await ApplyAsync(ticketId, """{ "kind": "set_pending_till", "relative": "P2D" }""");

        Assert.Equal(TriggerActionStatus.Failed, result.Status);
        Assert.Null(await PendingTillAsync(ticketId));
    }

    [PostgresFact]
    public async Task A_fixed_value_on_a_pending_ticket_is_written()
    {
        var ticketId = await TicketAsync("Pending");

        var result = await ApplyAsync(ticketId, """{ "kind": "set_pending_till", "relative": "P2D" }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        Assert.NotNull(await PendingTillAsync(ticketId));
    }

    [PostgresFact]
    public async Task Clearing_works_on_any_ticket()
    {
        var ticketId = await TicketAsync("Open");

        var result = await ApplyAsync(ticketId, """{ "kind": "set_pending_till", "clear": true }""");

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
    }

    // ---- one-shot cleanup in the bootstrapper -------------------------------

    [PostgresFact]
    public async Task The_bootstrap_cleanup_clears_leaked_values_and_keeps_pending_ones()
    {
        var leaked = await TicketAsync("Open");
        var held = await TicketAsync("Pending");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                "UPDATE tickets SET pending_till_utc = now() + interval '2 days' WHERE id = ANY(@ids)",
                new { ids = new[] { leaked, held } });
            await c.ExecuteAsync("DELETE FROM settings WHERE key = 'Bootstrap.PendingTillInvariantCleaned'");
        }

        await new Servicedesk.Infrastructure.Persistence.DatabaseBootstrapper(
            _db.DataSource, Microsoft.Extensions.Logging.Abstractions.NullLogger<Servicedesk.Infrastructure.Persistence.DatabaseBootstrapper>.Instance)
            .StartAsync(default);

        Assert.Null(await PendingTillAsync(leaked));
        Assert.NotNull(await PendingTillAsync(held));
        await using var check = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await check.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM settings WHERE key = 'Bootstrap.PendingTillInvariantCleaned'"));
    }

    // ---- plumbing -----------------------------------------------------------

    private async Task<Guid> TicketAsync(string category)
    {
        var seed = new DbSeed(_db.DataSource);
        var queue = await seed.QueueAsync();
        var status = await seed.StatusAsync(category);
        return await seed.TicketAsync("Reminder", await seed.ContactAsync(), queue, status, await seed.PriorityAsync());
    }

    private async Task<TriggerActionResult> ApplyAsync(Guid ticketId, string actionJson)
    {
        var tickets = TriggerWriteKit.Tickets(_db.DataSource);
        var handler = new SetPendingTillHandler(
            new SystemFieldMutator(_db.DataSource), null!, tickets, new TaxonomyRepository(_db.DataSource));
        var ticket = (await tickets.GetCoreAsync(ticketId, default))!;
        using var action = JsonDocument.Parse(actionJson);
        return await handler.ApplyAsync(action.RootElement,
            new TriggerEvaluationContext(ticketId, ticket, null, TriggerChangeSet.Empty, DateTime.UtcNow, Guid.NewGuid()), default);
    }

    private async Task<DateTime?> PendingTillAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<DateTime?>(
            "SELECT pending_till_utc FROM tickets WHERE id = @ticketId", new { ticketId });
    }
}
