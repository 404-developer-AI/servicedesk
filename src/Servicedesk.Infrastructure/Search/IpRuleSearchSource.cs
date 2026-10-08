using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for IP block/whitelist rules and open block
/// proposals. Admin-only: agents and customers never see a hit (security
/// configuration). Matches the address text and the rule reason; every hit
/// opens Settings → IP blocking filtered to that address.
public sealed class IpRuleSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public IpRuleSearchSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string Kind => SearchSourceKind.IpRules;

    public bool IsAvailableFor(SearchPrincipal principal) => principal.IsAdmin;

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Rules first, then open proposals for addresses without a rule.
        const string sql = """
            WITH hits AS (
                SELECT r.ip                                   AS ip,
                       r.kind                                 AS kind,
                       r.reason                               AS reason,
                       r.created_utc                          AS ts,
                       CASE WHEN r.ip ILIKE @prefix THEN 3.0 ELSE 1.0 END AS rank
                  FROM security_ip_rules r
                 WHERE r.ip ILIKE @like OR r.reason ILIKE @like
                UNION ALL
                SELECT p.ip, 'proposal', array_to_string(p.reasons, ', '), p.created_utc,
                       CASE WHEN p.ip ILIKE @prefix THEN 2.5 ELSE 0.5 END
                  FROM security_ip_proposals p
                 WHERE p.status = 'open'
                   AND p.ip ILIKE @like
                   AND NOT EXISTS (SELECT 1 FROM security_ip_rules r2 WHERE r2.ip = p.ip)
            )
            SELECT ip     AS Ip,
                   kind   AS Kind,
                   reason AS Reason,
                   rank::double precision AS Rank,
                   COUNT(*) OVER () AS TotalHits
              FROM hits
             ORDER BY rank DESC, ts DESC
             LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            prefix = EscapeLike(normalized) + "%",
            like = "%" + EscapeLike(normalized) + "%",
            limit,
            offset,
        }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Ip,
            Title: r.Ip,
            Snippet: r.Kind switch
            {
                "block" => string.IsNullOrEmpty(r.Reason) ? "Blocked" : $"Blocked · {r.Reason}",
                "whitelist" => string.IsNullOrEmpty(r.Reason) ? "Whitelisted" : $"Whitelisted · {r.Reason}",
                _ => $"Block proposal · {r.Reason}",
            },
            Rank: r.Rank,
            Meta: new Dictionary<string, string?> { ["ip"] = r.Ip, ["ruleKind"] = r.Kind })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class Row
    {
        public string Ip { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
