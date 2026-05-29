using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Dapper-backed reader for the TRMM mirror tables. Server-side filter +
/// sort + paginate so the Assets page stays snappy even on tens of
/// thousands of agents.
public sealed class AssetRepository : IAssetRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AssetRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<AssetListResult> ListAsync(AssetListQuery query, int warnThresholdDays, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var offset = (page - 1) * pageSize;
        var threshold = Math.Clamp(warnThresholdDays, 1, 3650);

        var (where, parameters) = BuildWhere(query);
        var orderBy = BuildOrderBy(query.Sort);

        // SECURITY: All filter values are passed as parameters; the only
        // string concat happens on whitelisted WHERE-flags + ORDER BY
        // tokens, both derived from non-user-controlled enums.
        //
        // The EOL classifier:
        //   - Derives a cycle key compatible with the endoflife.date
        //     registry per agent_type:
        //       Windows 11/10  + os_build → "11-24h2", "10-22h2", …
        //       Windows 7/8/8.1/Vista/XP → "7", "8.1", …
        //       Windows Server YYYY[ R2] → "2022", "2012-r2", …
        //   - LEFT JOINs eol_releases on (product, cycle).
        //   - Classifies the resolved eol_utc into active / soon /
        //     expired / unknown given the admin-configured threshold.
        var sql = $"""
            WITH enriched AS (
                SELECT a.*,
                       c.name        AS client_name,
                       c.code        AS client_code,
                       c.company_id  AS company_id,
                       co.name       AS company_name,
                       s.name        AS site_name,
                       CASE
                         WHEN a.os_family ILIKE 'Windows Server%' THEN 'windows-server'
                         WHEN a.os_family IN
                           ('Windows 11','Windows 10','Windows 8.1','Windows 8','Windows 7','Windows Vista','Windows XP')
                           THEN 'windows'
                         ELSE NULL
                       END AS eol_product,
                       -- endoflife.date splits Windows 10/11 by SKU.
                       -- "-iot-lts" wins over "-e-lts" wins over "-e" wins
                       -- over "-w" so a Enterprise-LTSC string doesn't
                       -- collapse to plain "-e" by mistake.
                       CASE
                         WHEN a.os_family IN ('Windows 11','Windows 10') AND a.os_name IS NOT NULL THEN
                           CASE
                             WHEN a.os_name ILIKE '%IoT%LTSC%' OR a.os_name ILIKE '%IoT%LTSB%'
                               THEN '-iot-lts'
                             WHEN a.os_name ILIKE '%LTSC%' OR a.os_name ILIKE '%LTSB%'
                               THEN '-e-lts'
                             WHEN a.os_name ILIKE '%Enterprise%' OR a.os_name ILIKE '%Education%'
                               THEN '-e'
                             WHEN a.os_name ILIKE '%Pro%' OR a.os_name ILIKE '%Home%'
                               THEN '-w'
                             ELSE ''
                           END
                         ELSE ''
                       END AS eol_sku_suffix
                  FROM trmm_agents a
                  JOIN trmm_clients c ON c.trmm_client_id = a.trmm_client_id
                  JOIN trmm_sites   s ON s.trmm_site_id   = a.trmm_site_id
                  LEFT JOIN companies co ON co.id = c.company_id
            ),
            cycled AS (
                SELECT e.*,
                       -- Primary cycle: SKU-suffixed for Windows 10/11,
                       -- canonical SP-form for the legacy releases.
                       CASE
                         WHEN e.os_family ILIKE 'Windows Server%' THEN
                           lower(replace(regexp_replace(e.os_family, '^Windows Server ', ''), ' ', '-'))
                         WHEN e.os_family IN ('Windows 11','Windows 10') AND e.os_build IS NOT NULL THEN
                           replace(lower(e.os_family), 'windows ', '') || '-' || lower(e.os_build) || e.eol_sku_suffix
                         WHEN e.os_family = 'Windows 8.1' THEN '8.1'
                         WHEN e.os_family = 'Windows 8'   THEN '8'
                         WHEN e.os_family = 'Windows 7'   THEN '7-sp1'
                         WHEN e.os_family = 'Windows Vista' THEN '6-sp2'
                         WHEN e.os_family = 'Windows XP'  THEN '5-sp3'
                         ELSE NULL
                       END AS eol_cycle_primary,
                       -- Fallback cycle: no SKU suffix. Catches Windows 10
                       -- 22H2 which has a single registry entry "10-22h2"
                       -- (no split). For SKUs that DO split (Win 11, older
                       -- Win 10) the primary already matches so the
                       -- fallback is harmless.
                       CASE
                         WHEN e.os_family IN ('Windows 11','Windows 10') AND e.os_build IS NOT NULL THEN
                           replace(lower(e.os_family), 'windows ', '') || '-' || lower(e.os_build)
                         ELSE NULL
                       END AS eol_cycle_fallback
                  FROM enriched e
            ),
            joined AS (
                SELECT c.*,
                       COALESCE(er1.eol_utc, er2.eol_utc) AS eol_release_utc,
                       CASE
                         WHEN COALESCE(er1.eol_utc, er2.eol_utc) IS NULL THEN 'unknown'
                         WHEN COALESCE(er1.eol_utc, er2.eol_utc) < now() THEN 'expired'
                         WHEN COALESCE(er1.eol_utc, er2.eol_utc) < now() + (@thresholdDays * interval '1 day') THEN 'soon'
                         ELSE 'active'
                       END AS eol_status
                  FROM cycled c
                  LEFT JOIN eol_releases er1
                       ON er1.product = c.eol_product AND er1.cycle = c.eol_cycle_primary
                  LEFT JOIN eol_releases er2
                       ON er2.product = c.eol_product AND er2.cycle = c.eol_cycle_fallback
            ),
            filtered AS (
                SELECT * FROM joined
                 {where}
            ),
            total AS (
                SELECT count(*)::int AS n FROM filtered
            )
            SELECT id              AS Id,
                   trmm_agent_id   AS TrmmAgentId,
                   hostname        AS Hostname,
                   agent_type      AS AgentType,
                   os_name         AS OsName,
                   os_family       AS OsFamily,
                   os_build        AS OsBuild,
                   last_seen_utc   AS LastSeenUtc,
                   online          AS Online,
                   public_ip       AS PublicIp,
                   trmm_client_id  AS TrmmClientId,
                   client_name     AS ClientName,
                   client_code     AS ClientCode,
                   company_id      AS CompanyId,
                   company_name    AS CompanyName,
                   trmm_site_id    AS TrmmSiteId,
                   site_name       AS SiteName,
                   eol_release_utc AS EolUtc,
                   eol_status      AS EolStatus,
                   (SELECT n FROM total) AS TotalHits
              FROM filtered
             {orderBy}
             LIMIT @limit OFFSET @offset
            """;

        parameters.Add("limit", pageSize);
        parameters.Add("offset", offset);
        parameters.Add("thresholdDays", threshold);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<AssetListItemRow>(
            new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

        var total = rows.Count > 0 ? rows[0].TotalHits : 0;
        return new AssetListResult(rows.Select(r => r.ToItem()).ToList(), total);
    }

    public async Task<IReadOnlyList<string>> DistinctBuildsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT os_build
              FROM trmm_agents
             WHERE os_build IS NOT NULL AND os_build <> ''
             ORDER BY os_build DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<string>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<AssetDetail?> GetByIdAsync(Guid id, int warnThresholdDays, CancellationToken ct)
    {
        var threshold = Math.Clamp(warnThresholdDays, 1, 3650);
        const string sql = """
            WITH enriched AS (
                SELECT a.*,
                       c.name        AS client_name,
                       c.code        AS client_code,
                       c.company_id  AS company_id,
                       co.name       AS company_name,
                       s.name        AS site_name,
                       CASE
                         WHEN a.os_family ILIKE 'Windows Server%' THEN 'windows-server'
                         WHEN a.os_family IN
                           ('Windows 11','Windows 10','Windows 8.1','Windows 8','Windows 7','Windows Vista','Windows XP')
                           THEN 'windows'
                         ELSE NULL
                       END AS eol_product,
                       CASE
                         WHEN a.os_family IN ('Windows 11','Windows 10') AND a.os_name IS NOT NULL THEN
                           CASE
                             WHEN a.os_name ILIKE '%IoT%LTSC%' OR a.os_name ILIKE '%IoT%LTSB%'
                               THEN '-iot-lts'
                             WHEN a.os_name ILIKE '%LTSC%' OR a.os_name ILIKE '%LTSB%'
                               THEN '-e-lts'
                             WHEN a.os_name ILIKE '%Enterprise%' OR a.os_name ILIKE '%Education%'
                               THEN '-e'
                             WHEN a.os_name ILIKE '%Pro%' OR a.os_name ILIKE '%Home%'
                               THEN '-w'
                             ELSE ''
                           END
                         ELSE ''
                       END AS eol_sku_suffix
                  FROM trmm_agents a
                  JOIN trmm_clients c ON c.trmm_client_id = a.trmm_client_id
                  JOIN trmm_sites   s ON s.trmm_site_id   = a.trmm_site_id
                  LEFT JOIN companies co ON co.id = c.company_id
                 WHERE a.id = @id
            ),
            cycled AS (
                SELECT e.*,
                       CASE
                         WHEN e.os_family ILIKE 'Windows Server%' THEN
                           lower(replace(regexp_replace(e.os_family, '^Windows Server ', ''), ' ', '-'))
                         WHEN e.os_family IN ('Windows 11','Windows 10') AND e.os_build IS NOT NULL THEN
                           replace(lower(e.os_family), 'windows ', '') || '-' || lower(e.os_build) || e.eol_sku_suffix
                         WHEN e.os_family = 'Windows 8.1' THEN '8.1'
                         WHEN e.os_family = 'Windows 8'   THEN '8'
                         WHEN e.os_family = 'Windows 7'   THEN '7-sp1'
                         WHEN e.os_family = 'Windows Vista' THEN '6-sp2'
                         WHEN e.os_family = 'Windows XP'  THEN '5-sp3'
                         ELSE NULL
                       END AS eol_cycle_primary,
                       CASE
                         WHEN e.os_family IN ('Windows 11','Windows 10') AND e.os_build IS NOT NULL THEN
                           replace(lower(e.os_family), 'windows ', '') || '-' || lower(e.os_build)
                         ELSE NULL
                       END AS eol_cycle_fallback
                  FROM enriched e
            )
            SELECT c.id              AS Id,
                   c.trmm_agent_id   AS TrmmAgentId,
                   c.hostname        AS Hostname,
                   c.agent_type      AS AgentType,
                   c.os_name         AS OsName,
                   c.os_family       AS OsFamily,
                   c.os_build        AS OsBuild,
                   c.last_seen_utc   AS LastSeenUtc,
                   c.online          AS Online,
                   c.public_ip       AS PublicIp,
                   c.trmm_client_id  AS TrmmClientId,
                   c.client_name     AS ClientName,
                   c.client_code     AS ClientCode,
                   c.company_id      AS CompanyId,
                   c.company_name    AS CompanyName,
                   c.trmm_site_id    AS TrmmSiteId,
                   c.site_name       AS SiteName,
                   c.created_utc     AS CreatedUtc,
                   c.updated_utc     AS UpdatedUtc,
                   c.last_sync_utc   AS LastSyncUtc,
                   COALESCE(er1.eol_utc, er2.eol_utc) AS EolUtc,
                   CASE
                     WHEN COALESCE(er1.eol_utc, er2.eol_utc) IS NULL THEN 'unknown'
                     WHEN COALESCE(er1.eol_utc, er2.eol_utc) < now() THEN 'expired'
                     WHEN COALESCE(er1.eol_utc, er2.eol_utc) < now() + (@thresholdDays * interval '1 day') THEN 'soon'
                     ELSE 'active'
                   END               AS EolStatus
              FROM cycled c
              LEFT JOIN eol_releases er1
                   ON er1.product = c.eol_product AND er1.cycle = c.eol_cycle_primary
              LEFT JOIN eol_releases er2
                   ON er2.product = c.eol_product AND er2.cycle = c.eol_cycle_fallback
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AssetDetail>(
            new CommandDefinition(sql, new { id, thresholdDays = threshold }, cancellationToken: ct));
    }

    public async Task<TrmmSyncStateRow> GetSyncStateAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT last_sync_utc   AS LastSyncUtc,
                   last_status     AS LastStatus,
                   last_error      AS LastError,
                   last_counts::text AS LastCountsJson
              FROM trmm_sync_state
             WHERE id = 'singleton'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<TrmmSyncStateRow>(
            new CommandDefinition(sql, cancellationToken: ct));
        return row ?? new TrmmSyncStateRow();
    }

    public async Task<IReadOnlyList<AssetClientMappingRow>> ListClientMappingsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT t.trmm_client_id  AS TrmmClientId,
                   t.name            AS Name,
                   t.code            AS Code,
                   t.auto_matched    AS AutoMatched,
                   t.company_id      AS CompanyId,
                   co.name           AS CompanyName,
                   co.code::text     AS CompanyCode,
                   (SELECT count(*)::int FROM trmm_agents a
                     WHERE a.trmm_client_id = t.trmm_client_id) AS AgentCount
              FROM trmm_clients t
              LEFT JOIN companies co ON co.id = t.company_id
             ORDER BY t.name ASC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AssetClientMappingRow>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<bool> SetClientMappingAsync(
        long trmmClientId,
        Guid? companyId,
        bool clearOverride,
        CancellationToken ct)
    {
        // clearOverride = true → wipe the manual pin and let the next
        // sync re-derive the link from [code] matching.
        var sql = clearOverride
            ? """
                UPDATE trmm_clients SET
                    auto_matched = TRUE,
                    company_id   = NULL,
                    updated_utc  = now()
                 WHERE trmm_client_id = @trmmClientId
              """
            : """
                UPDATE trmm_clients SET
                    auto_matched = FALSE,
                    company_id   = @companyId,
                    updated_utc  = now()
                 WHERE trmm_client_id = @trmmClientId
              """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { trmmClientId, companyId }, cancellationToken: ct));
        return affected > 0;
    }

    // ---- helpers -------------------------------------------------------

    private static (string Where, DynamicParameters Parameters) BuildWhere(AssetListQuery q)
    {
        var p = new DynamicParameters();
        var clauses = new List<string>(7);

        // Filters apply on the `joined` CTE, where all base columns sit
        // at the top level (no aliases). Hostname/client/site/etc. live
        // there directly.
        if (!string.IsNullOrWhiteSpace(q.Type))
        {
            clauses.Add("agent_type = @type");
            p.Add("type", q.Type);
        }
        if (q.Builds is { Count: > 0 })
        {
            clauses.Add("os_build = ANY(@builds)");
            p.Add("builds", q.Builds.ToArray());
        }
        if (q.CompanyIds is { Count: > 0 })
        {
            clauses.Add("company_id = ANY(@companyIds)");
            p.Add("companyIds", q.CompanyIds.ToArray());
        }
        if (q.OnlineOnly == true)
        {
            clauses.Add("online = TRUE");
        }
        if (!string.IsNullOrWhiteSpace(q.EolStatus))
        {
            clauses.Add("eol_status = @eolStatus");
            p.Add("eolStatus", q.EolStatus);
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            clauses.Add("""
                (lower(hostname) LIKE '%' || lower(@search) || '%'
                 OR lower(coalesce(client_name,'')) LIKE '%' || lower(@search) || '%'
                 OR lower(coalesce(client_code,'')) LIKE '%' || lower(@search) || '%'
                 OR lower(coalesce(site_name,'')) LIKE '%' || lower(@search) || '%')
                """);
            p.Add("search", q.Search.Trim());
        }

        var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
        return (where, p);
    }

    /// Whitelist sort tokens — never feed user input directly into the SQL.
    /// The frontend sends one of these stable identifiers; anything else
    /// falls back to the default (newest build first).
    private static string BuildOrderBy(string sort) => sort switch
    {
        "hostname_asc"  => "ORDER BY hostname ASC",
        "hostname_desc" => "ORDER BY hostname DESC",
        "build_asc"     => "ORDER BY os_build ASC NULLS LAST, hostname ASC",
        "build_desc"    => "ORDER BY os_build DESC NULLS LAST, hostname ASC",
        "last_seen_asc" => "ORDER BY last_seen_utc ASC NULLS LAST, hostname ASC",
        "last_seen_desc"=> "ORDER BY last_seen_utc DESC NULLS LAST, hostname ASC",
        "client_asc"    => "ORDER BY client_name ASC, hostname ASC",
        "client_desc"   => "ORDER BY client_name DESC, hostname ASC",
        _               => "ORDER BY os_build DESC NULLS LAST, hostname ASC",
    };

    private sealed class AssetListItemRow : AssetListItem
    {
        public int TotalHits { get; set; }

        public AssetListItem ToItem() => new()
        {
            Id = Id,
            TrmmAgentId = TrmmAgentId,
            Hostname = Hostname,
            AgentType = AgentType,
            OsName = OsName,
            OsFamily = OsFamily,
            OsBuild = OsBuild,
            LastSeenUtc = LastSeenUtc,
            Online = Online,
            PublicIp = PublicIp,
            TrmmClientId = TrmmClientId,
            ClientName = ClientName,
            ClientCode = ClientCode,
            CompanyId = CompanyId,
            CompanyName = CompanyName,
            TrmmSiteId = TrmmSiteId,
            SiteName = SiteName,
            EolUtc = EolUtc,
            EolStatus = EolStatus,
        };
    }
}
