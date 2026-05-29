using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Pulls one snapshot of clients/sites/agents from TRMM and upserts the
/// three local mirror tables. Per project convention upserts use
/// <c>INSERT … ON CONFLICT … DO UPDATE SET … RETURNING id</c> so concurrent
/// ticks (or a manual <c>Sync now</c> overlapping a scheduled tick) cannot
/// race each other to insert duplicate rows for the same TRMM id.
///
/// Client → Company auto-match runs against the
/// <c>[CODE] Name</c> prefix on the TRMM client name (extracted by the
/// API parser). Admin-overridden mappings — rows where
/// <c>auto_matched = FALSE</c> — are pinned and never overwritten by a
/// re-sync.
public sealed class TrmmSyncService : ITrmmSyncService
{
    private readonly ITrmmApiClient _api;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<TrmmSyncService> _logger;

    public TrmmSyncService(
        ITrmmApiClient api,
        NpgsqlDataSource dataSource,
        IIntegrationAuditLogger audit,
        ILogger<TrmmSyncService> logger)
    {
        _api = api;
        _dataSource = dataSource;
        _audit = audit;
        _logger = logger;
    }

    public async Task<TrmmSyncOutcome> RunOnceAsync(string trigger, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await _audit.LogAsync(new IntegrationAuditEvent(
            Integration: TrmmEventTypes.Integration,
            EventType: TrmmEventTypes.SyncStarted,
            Outcome: IntegrationAuditOutcome.Ok,
            Payload: new { trigger }), ct);

        try
        {
            var snapshot = await _api.ListClientsAndSitesAsync(ct);
            var rawAgents = await _api.ListAgentsAsync(ct);

            // TRMM's /agents/ listing emits client_name + site_name as
            // strings without the matching ids. Build name→id maps from
            // the snapshot we just pulled so the agent parser's name-only
            // rows can be resolved before upsert. Case-insensitive on
            // both sides because TRMM admins occasionally case-flip a
            // site name (e.g. "Main Office" → "Main office") between
            // releases without changing the underlying id.
            var clientByName = BuildNameIndex(snapshot.Clients.Select(c => (c.Name, c.Id)));
            var sitesByClient = snapshot.Sites
                .GroupBy(s => s.ClientId)
                .ToDictionary(
                    g => g.Key,
                    g => BuildNameIndex(g.Select(s => (s.Name, s.Id))));

            var agents = ResolveAgentLinks(rawAgents, clientByName, sitesByClient);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);

            var (clientCount, autoLinked) = await UpsertClientsAsync(connection, snapshot.Clients, ct);
            var siteCount = await UpsertSitesAsync(connection, snapshot.Sites, ct);
            var agentCount = await UpsertAgentsAsync(connection, agents, ct);

            await PruneRemovedAsync(connection,
                snapshot.Clients.Select(c => c.Id),
                snapshot.Sites.Select(s => s.Id),
                agents.Select(a => a.AgentId),
                ct);

            stopwatch.Stop();
            var outcome = new TrmmSyncOutcome(
                Success: true,
                Clients: clientCount,
                Sites: siteCount,
                Agents: agentCount,
                AutoLinkedCompanies: autoLinked,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: null,
                ErrorMessage: null);

            await WriteSyncStateAsync(connection, outcome, ct);

            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: TrmmEventTypes.SyncCompleted,
                Outcome: IntegrationAuditOutcome.Ok,
                LatencyMs: outcome.LatencyMs,
                Payload: new
                {
                    trigger,
                    clients = outcome.Clients,
                    sites = outcome.Sites,
                    agents = outcome.Agents,
                    autoLinkedCompanies = outcome.AutoLinkedCompanies,
                }), ct);

            return outcome;
        }
        catch (TrmmApiException ex)
        {
            stopwatch.Stop();
            var outcome = new TrmmSyncOutcome(
                Success: false,
                Clients: 0, Sites: 0, Agents: 0, AutoLinkedCompanies: 0,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: ex.UpstreamErrorCode ?? "transport_error",
                ErrorMessage: ex.Message);

            await TryPersistFailureStateAsync(outcome, ct);

            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: TrmmEventTypes.SyncFailed,
                Outcome: IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: outcome.ErrorCode,
                Payload: new { trigger, message = outcome.ErrorMessage }), ct);

            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "TRMM sync threw an unexpected exception.");
            var outcome = new TrmmSyncOutcome(
                Success: false,
                Clients: 0, Sites: 0, Agents: 0, AutoLinkedCompanies: 0,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "internal_error",
                ErrorMessage: ex.Message);

            await TryPersistFailureStateAsync(outcome, ct);

            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: TrmmEventTypes.SyncFailed,
                Outcome: IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: "internal_error",
                Payload: new { trigger, message = ex.Message }), ct);

            return outcome;
        }
    }

    // ---- Upserts -------------------------------------------------------

    private static async Task<(int upserted, int autoLinked)> UpsertClientsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<TrmmClient> clients,
        CancellationToken ct)
    {
        if (clients.Count == 0) return (0, 0);

        // Pre-resolve [code] → companies.id lookups in a single round-trip.
        // companies.code is CITEXT so the comparison is case-insensitive
        // out of the box; we only feed non-empty codes to the lookup.
        var codes = clients
            .Where(c => !string.IsNullOrEmpty(c.Code))
            .Select(c => c.Code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var codeToCompanyId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (codes.Length > 0)
        {
            var rows = await connection.QueryAsync<(string Code, Guid Id)>(
                new CommandDefinition(
                    "SELECT code AS Code, id AS Id FROM companies WHERE code = ANY(@codes)",
                    new { codes },
                    cancellationToken: ct));
            foreach (var row in rows) codeToCompanyId[row.Code] = row.Id;
        }

        const string upsertSql = """
            INSERT INTO trmm_clients
                (trmm_client_id, name, code, company_id, auto_matched, created_utc, updated_utc)
            VALUES
                (@trmmClientId, @name, @code, @companyId, @autoMatched, now(), now())
            ON CONFLICT (trmm_client_id) DO UPDATE SET
                name        = EXCLUDED.name,
                code        = EXCLUDED.code,
                -- Pinned (auto_matched = FALSE) rows keep their admin-set
                -- company_id; auto-matched rows track the resolved code.
                company_id  = CASE WHEN trmm_clients.auto_matched
                                   THEN EXCLUDED.company_id
                                   ELSE trmm_clients.company_id END,
                updated_utc = now()
            RETURNING id, (xmax = 0) AS Inserted, (company_id IS NOT NULL AND auto_matched) AS AutoLinked
            """;

        var inserted = 0;
        var autoLinked = 0;
        foreach (var client in clients)
        {
            Guid? companyId = null;
            if (!string.IsNullOrEmpty(client.Code)
                && codeToCompanyId.TryGetValue(client.Code, out var resolved))
            {
                companyId = resolved;
            }

            var result = await connection.QuerySingleAsync<(Guid Id, bool Inserted, bool AutoLinked)>(
                new CommandDefinition(upsertSql, new
                {
                    trmmClientId = client.Id,
                    name = client.Name,
                    code = client.Code,
                    companyId,
                    autoMatched = true,
                }, cancellationToken: ct));
            inserted++;
            if (result.AutoLinked) autoLinked++;
        }
        return (inserted, autoLinked);
    }

    private static async Task<int> UpsertSitesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<TrmmSite> sites,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO trmm_sites
                (trmm_site_id, trmm_client_id, name, created_utc, updated_utc)
            VALUES
                (@trmmSiteId, @trmmClientId, @name, now(), now())
            ON CONFLICT (trmm_site_id) DO UPDATE SET
                trmm_client_id = EXCLUDED.trmm_client_id,
                name           = EXCLUDED.name,
                updated_utc    = now()
            RETURNING id
            """;
        var count = 0;
        foreach (var site in sites)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                trmmSiteId = site.Id,
                trmmClientId = site.ClientId,
                name = site.Name,
            }, cancellationToken: ct));
            count++;
        }
        return count;
    }

    /// Case-insensitive name → id dictionary. Duplicate names collapse —
    /// the first occurrence wins so re-running is deterministic.
    private static Dictionary<string, long> BuildNameIndex(IEnumerable<(string Name, long Id)> pairs)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, id) in pairs)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            map.TryAdd(name.Trim(), id);
        }
        return map;
    }

    /// Resolves agent rows that only carry <c>client_name</c>/<c>site_name</c>
    /// strings (the shape TRMM's listing endpoint emits) by looking up
    /// the matching id in the snapshot we just upserted. Agents that
    /// can't be resolved on either side are dropped — the schema requires
    /// non-null FKs.
    private static IReadOnlyList<TrmmAgent> ResolveAgentLinks(
        IReadOnlyList<TrmmAgent> raw,
        Dictionary<string, long> clientByName,
        Dictionary<long, Dictionary<string, long>> sitesByClient)
    {
        var resolved = new List<TrmmAgent>(raw.Count);
        foreach (var a in raw)
        {
            var clientId = a.ClientId;
            if (clientId is null && a.ClientName is { Length: > 0 } cn
                && clientByName.TryGetValue(cn, out var cid))
            {
                clientId = cid;
            }
            if (clientId is null) continue;

            var siteId = a.SiteId;
            if (siteId is null
                && a.SiteName is { Length: > 0 } sn
                && sitesByClient.TryGetValue(clientId.Value, out var siteMap)
                && siteMap.TryGetValue(sn, out var sid))
            {
                siteId = sid;
            }
            if (siteId is null) continue;

            resolved.Add(a with { ClientId = clientId, SiteId = siteId });
        }
        return resolved;
    }

    private static async Task<int> UpsertAgentsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<TrmmAgent> agents,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO trmm_agents
                (trmm_agent_id, hostname, agent_type, os_name, os_family, os_build,
                 last_seen_utc, online, public_ip,
                 trmm_client_id, trmm_site_id,
                 created_utc, updated_utc, last_sync_utc)
            VALUES
                (@trmmAgentId, @hostname, @agentType, @osName, @osFamily, @osBuild,
                 @lastSeenUtc, @online, @publicIp,
                 @trmmClientId, @trmmSiteId,
                 now(), now(), now())
            ON CONFLICT (trmm_agent_id) DO UPDATE SET
                hostname       = EXCLUDED.hostname,
                agent_type     = EXCLUDED.agent_type,
                os_name        = EXCLUDED.os_name,
                os_family      = EXCLUDED.os_family,
                os_build       = EXCLUDED.os_build,
                last_seen_utc  = EXCLUDED.last_seen_utc,
                online         = EXCLUDED.online,
                public_ip      = EXCLUDED.public_ip,
                trmm_client_id = EXCLUDED.trmm_client_id,
                trmm_site_id   = EXCLUDED.trmm_site_id,
                updated_utc    = now(),
                last_sync_utc  = now()
            RETURNING id
            """;
        var count = 0;
        foreach (var agent in agents)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                trmmAgentId = agent.AgentId,
                hostname = agent.Hostname,
                agentType = agent.AgentType,
                osName = agent.OsName,
                osFamily = agent.OsFamily,
                osBuild = agent.OsBuild,
                lastSeenUtc = agent.LastSeenUtc,
                online = agent.Online,
                publicIp = agent.PublicIp,
                trmmClientId = agent.ClientId,
                trmmSiteId = agent.SiteId,
            }, cancellationToken: ct));
            count++;
        }
        return count;
    }

    /// Removes mirror rows whose TRMM-id is no longer present in the
    /// upstream snapshot. Keeps the local DB convergent with TRMM
    /// without leaving orphan rows behind a client/agent decommission.
    private static async Task PruneRemovedAsync(
        NpgsqlConnection connection,
        IEnumerable<long> liveClientIds,
        IEnumerable<long> liveSiteIds,
        IEnumerable<string> liveAgentIds,
        CancellationToken ct)
    {
        var clientArr = liveClientIds.ToArray();
        var siteArr = liveSiteIds.ToArray();
        var agentArr = liveAgentIds.ToArray();

        if (agentArr.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM trmm_agents WHERE NOT (trmm_agent_id = ANY(@ids))",
                new { ids = agentArr }, cancellationToken: ct));
        }
        if (siteArr.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM trmm_sites WHERE NOT (trmm_site_id = ANY(@ids))",
                new { ids = siteArr }, cancellationToken: ct));
        }
        if (clientArr.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM trmm_clients WHERE NOT (trmm_client_id = ANY(@ids))",
                new { ids = clientArr }, cancellationToken: ct));
        }
    }

    private static async Task WriteSyncStateAsync(
        NpgsqlConnection connection, TrmmSyncOutcome outcome, CancellationToken ct)
    {
        var countsJson = JsonSerializer.Serialize(new
        {
            outcome.Clients,
            outcome.Sites,
            outcome.Agents,
            outcome.AutoLinkedCompanies,
            outcome.LatencyMs,
        });
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE trmm_sync_state SET
                last_sync_utc = now(),
                last_status   = @status,
                last_error    = @error,
                last_counts   = @counts::jsonb
            WHERE id = 'singleton'
            """,
            new
            {
                status = outcome.Success ? "ok" : "failed",
                error = outcome.Success ? null : outcome.ErrorMessage,
                counts = countsJson,
            },
            cancellationToken: ct));
    }

    private async Task TryPersistFailureStateAsync(TrmmSyncOutcome outcome, CancellationToken ct)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await WriteSyncStateAsync(connection, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist TRMM sync failure state.");
        }
    }
}
