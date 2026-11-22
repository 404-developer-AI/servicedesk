using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the Assets family (TRMM assets and Remote
/// Desktop notes): gated by the per-user `assets` flag, Admin always passes,
/// Customer never.
[Collection(PostgresCollection.Name)]
public sealed class AssetSearchSourcesDbTests
{
    private readonly PostgresDatabase _db;

    public AssetSearchSourcesDbTests(PostgresDatabase db) => _db = db;

    private static long NewTrmmId() => Random.Shared.NextInt64(1_000_000, 900_000_000);

    private async Task<long> SeedClientAsync(string name)
    {
        var clientId = NewTrmmId();
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "INSERT INTO trmm_clients (trmm_client_id, name) VALUES (@clientId, @name)",
            new { clientId, name });
        return clientId;
    }

    [PostgresFact]
    public async Task Assets_are_found_by_admin_and_assets_flag_holders_only()
    {
        var token = Token();
        var clientId = await SeedClientAsync("Example Company");
        var siteId = NewTrmmId();
        var agentId = Guid.NewGuid();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                "INSERT INTO trmm_sites (trmm_site_id, trmm_client_id, name) VALUES (@siteId, @clientId, 'Main office')",
                new { siteId, clientId });
            await c.ExecuteAsync("""
                INSERT INTO trmm_agents (trmm_agent_id, hostname, agent_type, trmm_client_id, trmm_site_id)
                VALUES (@trmmAgentId, @hostname, 'workstation', @clientId, @siteId)
                """,
                new { trmmAgentId = $"agent-{agentId:N}", hostname = token, clientId, siteId });
        }

        var source = new AssetSearchSource(_db.DataSource);

        var admin = await source.SearchAsync(Query(token), Admin(), default);
        Assert.Equal(new[] { token }, admin.Hits.Select(h => h.Title).ToArray());
        var holder = await source.SearchAsync(Query(token), Agent(SearchFeature.Assets), default);
        Assert.Equal(new[] { token }, holder.Hits.Select(h => h.Title).ToArray());

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.Contracts, SearchFeature.AdsolutOrders), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Assets), token);
    }

    [PostgresFact]
    public async Task Remote_desktop_notes_are_found_by_admin_and_assets_flag_holders_only()
    {
        var token = Token();
        var clientId = await SeedClientAsync("Example Company");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync(
                "INSERT INTO trmm_client_notes (trmm_client_id, body) VALUES (@clientId, @body)",
                new { clientId, body = $"{token} jump host credentials are in the vault" });

        var source = new RemoteDesktopNoteSearchSource(_db.DataSource);
        var expected = new[] { clientId.ToString() };

        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.Assets), default)));

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.Contracts), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Assets), token);
    }
}
