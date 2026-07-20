using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.52: Tactical RMM asset search. Backed by the local mirror tables
/// (<c>trmm_agents</c> + <c>trmm_clients</c> + <c>trmm_sites</c>); a
/// trigram index on <c>hostname</c> covers fuzzy matches, equality on
/// <c>os_build</c> and ILIKE on the client name/code round out the
/// matcher.
///
/// Row-level rules:
///   Customer principal → zero hits (the Assets page is Agent/Admin only
///                                   in v0.0.52; a Customer-portal view
///                                   can ship later with company-scoped
///                                   filtering layered on top of these
///                                   same rows).
///   Agent/Admin       → all rows. The Assets page is shared across the
///                       team — there is no per-user asset visibility.
public sealed class AssetSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public AssetSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Assets;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Three match-paths combined:
        //   1) hostname ILIKE %q% — substring + trigram-backed fast path.
        //   2) trigram similarity on hostname — catches typos.
        //   3) os_build / client name / client code / site name ILIKE.
        // Rank is hostname-similarity + a flat 0.4 bump on substring
        // matches so "win 11" surfaces hosts whose hostname contains the
        // token even when no trigram match crosses the threshold.
        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "last_seen_utc DESC NULLS LAST, id DESC",
            SearchSort.Oldest => "last_seen_utc ASC NULLS FIRST, id ASC",
            _ => "rank DESC, hostname",
        };

        var sql = $"""
            WITH q AS (
                SELECT lower(@query) AS norm
            ),
            hits AS (
                SELECT a.id,
                       a.trmm_agent_id,
                       a.hostname,
                       a.agent_type,
                       a.os_name,
                       a.os_build,
                       a.last_seen_utc,
                       c.name        AS client_name,
                       c.code        AS client_code,
                       s.name        AS site_name,
                       (
                           CASE WHEN lower(a.hostname) % (SELECT norm FROM q)
                                THEN similarity(lower(a.hostname), (SELECT norm FROM q))
                                ELSE 0 END
                         + CASE WHEN lower(a.hostname) LIKE '%' || (SELECT norm FROM q) || '%'
                                THEN 0.4 ELSE 0 END
                         + CASE WHEN lower(coalesce(c.name,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                                THEN 0.3 ELSE 0 END
                         + CASE WHEN lower(coalesce(c.code,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                                THEN 0.3 ELSE 0 END
                         + CASE WHEN lower(coalesce(a.os_build,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                                THEN 0.2 ELSE 0 END
                         + CASE WHEN lower(coalesce(s.name,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                                THEN 0.2 ELSE 0 END
                       )::double precision AS rank,
                       COUNT(*) OVER () AS total_hits
                  FROM trmm_agents a
                  JOIN trmm_clients c ON c.trmm_client_id = a.trmm_client_id
                  JOIN trmm_sites s   ON s.trmm_site_id   = a.trmm_site_id
                 WHERE (
                        lower(a.hostname) % (SELECT norm FROM q)
                     OR lower(a.hostname) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(a.os_build,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(c.name,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(c.code,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(s.name,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                       )
            )
            SELECT id              AS Id,
                   trmm_agent_id   AS TrmmAgentId,
                   hostname        AS Hostname,
                   agent_type      AS AgentType,
                   os_name         AS OsName,
                   os_build        AS OsBuild,
                   client_name     AS ClientName,
                   client_code     AS ClientCode,
                   site_name       AS SiteName,
                   rank            AS Rank,
                   total_hits      AS TotalHits
              FROM hits
             WHERE rank > 0
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<AssetHitRow>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Hostname,
            Snippet: BuildSnippet(r),
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["agentType"] = r.AgentType,
                ["osBuild"] = r.OsBuild,
                ["clientName"] = r.ClientName,
                ["clientCode"] = r.ClientCode,
                ["siteName"] = r.SiteName,
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private static string BuildSnippet(AssetHitRow r)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(r.OsBuild)) parts.Add(r.OsBuild!);
        else if (!string.IsNullOrWhiteSpace(r.OsName)) parts.Add(r.OsName!);
        if (!string.IsNullOrWhiteSpace(r.ClientName)) parts.Add(r.ClientName!);
        if (!string.IsNullOrWhiteSpace(r.SiteName)) parts.Add(r.SiteName!);
        return string.Join(" · ", parts);
    }

    private sealed class AssetHitRow
    {
        public Guid Id { get; set; }
        public string TrmmAgentId { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string AgentType { get; set; } = "";
        public string? OsName { get; set; }
        public string? OsBuild { get; set; }
        public string? ClientName { get; set; }
        public string? ClientCode { get; set; }
        public string? SiteName { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
