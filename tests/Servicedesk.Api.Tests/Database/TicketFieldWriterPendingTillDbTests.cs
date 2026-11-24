using Dapper;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The field writer keeps the pending-till invariant inside its row lock:
/// <c>pending_till_utc</c> is only written when the ticket is, or ends up,
/// in a Pending status (v0.0.100 — a value on any other ticket made the
/// scheduler re-evaluate it every tick). The rules refuse such a write
/// first; this pins the writer on its own, for any caller that skips them.
[Collection(PostgresCollection.Name)]
public sealed class TicketFieldWriterPendingTillDbTests
{
    private readonly PostgresDatabase _db;

    public TicketFieldWriterPendingTillDbTests(PostgresDatabase db) => _db = db;

    private static readonly DateTime Later = DateTime.UtcNow.AddDays(2);

    [PostgresFact]
    public async Task A_pending_till_on_a_ticket_that_stays_open_is_not_written()
    {
        var (seed, ticketId, _) = await TicketAsync("Open");

        var result = await WriteAsync(seed, ticketId, new TicketFieldUpdate(PendingTillUtc: Later));

        Assert.Equal(TicketFieldWriteStatus.NoOp, result.Status);
        Assert.Null(await PendingTillAsync(ticketId));
    }

    [PostgresFact]
    public async Task A_pending_till_on_a_pending_ticket_is_written()
    {
        var (seed, ticketId, _) = await TicketAsync("Pending");

        var result = await WriteAsync(seed, ticketId, new TicketFieldUpdate(PendingTillUtc: Later));

        Assert.Equal(TicketFieldWriteStatus.Applied, result.Status);
        Assert.NotNull(await PendingTillAsync(ticketId));
    }

    [PostgresFact]
    public async Task A_pending_till_with_a_move_into_pending_is_written()
    {
        var (seed, ticketId, _) = await TicketAsync("Open");
        var pending = await seed.StatusAsync("Pending");

        await WriteAsync(seed, ticketId, new TicketFieldUpdate(StatusId: pending, PendingTillUtc: Later));

        Assert.NotNull(await PendingTillAsync(ticketId));
    }

    [PostgresFact]
    public async Task A_pending_till_with_a_move_out_of_pending_is_dropped_and_the_column_cleared()
    {
        var (seed, ticketId, _) = await TicketAsync("Pending");
        await WriteAsync(seed, ticketId, new TicketFieldUpdate(PendingTillUtc: Later));
        var open = await seed.StatusAsync("Open");

        await WriteAsync(seed, ticketId, new TicketFieldUpdate(StatusId: open, PendingTillUtc: Later.AddDays(1)));

        Assert.Null(await PendingTillAsync(ticketId));
    }

    // ---- plumbing -----------------------------------------------------------

    private async Task<(DbSeed Seed, Guid TicketId, Guid Status)> TicketAsync(string category)
    {
        var seed = new DbSeed(_db.DataSource);
        var queue = await seed.QueueAsync();
        var status = await seed.StatusAsync(category);
        var ticketId = await seed.TicketAsync("Reminder", await seed.ContactAsync(), queue, status, await seed.PriorityAsync());
        return (seed, ticketId, status);
    }

    private async Task<TicketFieldWriteResult> WriteAsync(DbSeed seed, Guid ticketId, TicketFieldUpdate update) =>
        await TriggerWriteKit.Writer(_db.DataSource).ApplyAsync(
            ticketId, update, new TicketWriteOrigin.Agent(await seed.UserAsync()), default);

    private async Task<DateTime?> PendingTillAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<DateTime?>(
            "SELECT pending_till_utc FROM tickets WHERE id = @ticketId", new { ticketId });
    }
}
