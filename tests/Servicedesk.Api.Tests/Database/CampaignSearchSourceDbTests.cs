using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.67 — the Campaigns search source follows the per-user flag (admins
/// included): a flag holder finds campaigns by name or subject, everybody
/// else — customers too — gets zero hits without touching the database.
[Collection(PostgresCollection.Name)]
public sealed class CampaignSearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public CampaignSearchSourceDbTests(PostgresDatabase db) => _db = db;

    private static readonly IReadOnlySet<string> Flag = new HashSet<string> { SearchFeature.Campaigns };

    private static SearchRequest Query(string q) => new(q, null, 20, 0);

    [PostgresFact]
    public async Task Flag_holder_finds_a_campaign_by_name_and_subject()
    {
        var token = $"fwupd{Guid.NewGuid():N}"[..14];
        Guid byName, bySubject;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            byName = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO campaigns (name, subject) VALUES (@name, 'Update window') RETURNING id",
                new { name = $"Firewall {token}" });
            bySubject = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO campaigns (name, subject) VALUES ('Other round', @subject) RETURNING id",
                new { subject = $"Planned {token}" });
        }

        var source = new CampaignSearchSource(_db.DataSource);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(), Flag);

        var hits = await source.SearchAsync(Query(token), agent, default);

        Assert.Equal(
            new[] { byName.ToString(), bySubject.ToString() }.OrderBy(x => x),
            hits.Hits.Select(h => h.EntityId).OrderBy(x => x));
    }

    [PostgresTheory]
    [InlineData("Agent")]
    [InlineData("Admin")]
    public async Task Without_the_flag_nobody_gets_hits(string role)
    {
        var token = $"fwupd{Guid.NewGuid():N}"[..14];
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("INSERT INTO campaigns (name) VALUES (@name)", new { name = $"Firewall {token}" });

        var source = new CampaignSearchSource(_db.DataSource);
        var principal = new SearchPrincipal(Guid.NewGuid(), role, allowedQueueIds: null);

        Assert.False(source.IsAvailableFor(principal));
        Assert.Empty((await source.SearchAsync(Query(token), principal, default)).Hits);
    }

    [Fact]
    public async Task A_customer_with_the_flag_still_gets_nothing()
    {
        // null! data source: an unavailable principal must never reach the DB.
        var source = new CampaignSearchSource(null!);
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", Array.Empty<Guid>(), Flag);
        Assert.False(source.IsAvailableFor(customer));
        Assert.Empty((await source.SearchAsync(Query("firewall"), customer, default)).Hits);
    }
}
