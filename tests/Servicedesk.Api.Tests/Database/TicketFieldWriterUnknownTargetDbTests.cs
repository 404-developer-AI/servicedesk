using Dapper;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// An update naming a queue, status or priority that does not exist is a
/// clean TargetNotFound with nothing written — the agent path turns it into
/// <see cref="TicketFieldTargetNotFoundException"/> (PATCH 400, bulk skip),
/// never a foreign-key 500. The precheck does not catch these: an admin has
/// access to any queue id, and a queue without an allowed-status list
/// accepts any status id.
[Collection(PostgresCollection.Name)]
public sealed class TicketFieldWriterUnknownTargetDbTests
{
    private readonly PostgresDatabase _db;

    public TicketFieldWriterUnknownTargetDbTests(PostgresDatabase db) => _db = db;

    public static TheoryData<string> Targets => new() { "queue", "status", "priority" };

    [PostgresTheory]
    [MemberData(nameof(Targets))]
    public async Task An_unknown_target_id_is_reported_and_rolls_the_whole_update_back(string target)
    {
        var seed = new DbSeed(_db.DataSource);
        var queue = await seed.QueueAsync();
        var open = await seed.StatusAsync("Open");
        var ticketId = await seed.TicketAsync("Laptop setup", await seed.ContactAsync(), queue, open, await seed.PriorityAsync());
        var unknown = Guid.NewGuid();
        var update = target switch
        {
            "queue" => new TicketFieldUpdate(QueueId: unknown, Subject: "Changed"),
            "status" => new TicketFieldUpdate(StatusId: unknown, Subject: "Changed"),
            _ => new TicketFieldUpdate(PriorityId: unknown, Subject: "Changed"),
        };

        var result = await TriggerWriteKit.Writer(_db.DataSource).ApplyAsync(
            ticketId, update, new TicketWriteOrigin.Agent(await seed.UserAsync()), default);

        Assert.Equal(TicketFieldWriteStatus.TargetNotFound, result.Status);
        Assert.Equal(target, result.MissingTarget);
        await using var c = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal("Laptop setup", await c.ExecuteScalarAsync<string>(
            "SELECT subject FROM tickets WHERE id = @ticketId", new { ticketId }));
    }
}
