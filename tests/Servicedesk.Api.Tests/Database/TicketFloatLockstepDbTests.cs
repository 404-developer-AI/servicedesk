using System.Text.Json;
using Dapper;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// Lockstep between the server float ORDER BY (TicketRepository.SearchAsync)
/// and the client's floatBucket (lib/ticketFlags.ts). Both run the same cases
/// from src/Servicedesk.Web/src/lib/ticketFloatCases.json, so a change on one
/// side only turns one of the two tests red (CLAUDE.md: "server ORDER BY and
/// lib/ticketFlags.ts stay in lockstep").
[Collection(PostgresCollection.Name)]
public sealed class TicketFloatLockstepDbTests
{
    private readonly PostgresDatabase _db;

    public TicketFloatLockstepDbTests(PostgresDatabase db) => _db = db;

    private sealed record Case(
        string Name, bool PriorityIsDefault, bool IsCallback, bool IsResearch, string StatusStateCategory);

    private sealed record View(
        string Name, bool PriorityFloat, bool CallbackFloat, bool ResearchFloat, Dictionary<string, int?> Expected);

    private sealed record Cases(List<Case> Tickets, List<View> Views);

    private static Cases Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Servicedesk.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "Servicedesk.Web", "src", "lib", "ticketFloatCases.json");
        return JsonSerializer.Deserialize<Cases>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [PostgresFact]
    public async Task Server_float_order_matches_the_shared_cases()
    {
        var cases = Load();
        Assert.NotEmpty(cases.Tickets);
        Assert.NotEmpty(cases.Views);

        var seed = new DbSeed(_db.DataSource);
        var queue = await seed.QueueAsync();
        var contact = await seed.ContactAsync();
        var defaultPriority = await PriorityAsync(isDefault: true);
        var raisedPriority = await PriorityAsync(isDefault: false);
        var statuses = new Dictionary<string, Guid>();

        // Without floats the list is updated_utc DESC. Seed the timestamps so
        // that order is the reverse of the float order (the tickets that
        // should float highest are the oldest): a ticket the server floats
        // wrongly — or fails to float — then always lands out of sequence.
        var byExpectedFloat = cases.Tickets
            .OrderByDescending(t => Rank(cases.Views[0].Expected[t.Name]))
            .ToList();
        var ids = new Dictionary<Guid, string>();
        var baseUtc = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < byExpectedFloat.Count; i++)
        {
            var c = byExpectedFloat[i];
            if (!statuses.TryGetValue(c.StatusStateCategory, out var status))
            {
                status = await seed.StatusAsync(c.StatusStateCategory);
                statuses[c.StatusStateCategory] = status;
            }
            var id = await seed.TicketAsync(c.Name, contact, queue, status,
                c.PriorityIsDefault ? defaultPriority : raisedPriority);
            await using var conn = await _db.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE tickets SET is_callback = @cb, is_research = @rs, updated_utc = @at WHERE id = @id",
                new { cb = c.IsCallback, rs = c.IsResearch, at = baseUtc.AddMinutes(-i), id });
            ids[id] = c.Name;
        }

        var tickets = new TicketRepository(_db.DataSource);
        foreach (var view in cases.Views)
        {
            var page = await tickets.SearchAsync(
                new TicketQuery(
                    RequesterContactId: contact,
                    PriorityFloat: view.PriorityFloat,
                    CallbackFloat: view.CallbackFloat,
                    ResearchFloat: view.ResearchFloat),
                VisibilityScope.All, null, null, default);

            var names = page.Items.Select(item => ids[item.Id]).ToList();
            Assert.Equal(cases.Tickets.Count, names.Count);
            var ranks = names.Select(n => Rank(view.Expected[n])).ToList();
            for (var i = 1; i < ranks.Count; i++)
            {
                Assert.True(ranks[i - 1] <= ranks[i],
                    $"View \"{view.Name}\": server order [{string.Join(", ", names)}] puts {names[i - 1]} "
                    + $"(bucket {Show(view.Expected[names[i - 1]])}) above {names[i]} (bucket {Show(view.Expected[names[i]])}).");
            }
        }
    }

    private static int Rank(int? bucket) => bucket ?? 3;

    private static string Show(int? bucket) => bucket?.ToString() ?? "none";

    private async Task<Guid> PriorityAsync(bool isDefault)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO priorities (name, slug, level, is_default) VALUES (@name, @slug, 1, @isDefault) RETURNING id",
            new { name = $"Priority {suffix}", slug = $"p-{suffix}", isDefault });
    }
}
