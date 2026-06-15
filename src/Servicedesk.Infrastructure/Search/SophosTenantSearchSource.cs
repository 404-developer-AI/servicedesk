using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for the Sophos Central tenants surfaced under the
/// Contracts → Microsoft 365 matching module (the spam-filter overlay). Same
/// row-level authorization as the M365 mailbox source: a Customer principal
/// never sees hits; an Agent/Admin sees hits only when their own user row has
/// <c>contracts_enabled = TRUE</c>. Only tenants matched to a Servicedesk
/// company are searchable, so every hit links to that company's Microsoft 365
/// detail page (where the Protected/Unprotected column lives). Matches against
/// the Sophos display name (<c>showAs</c>) and the parsed customer code.
public sealed class SophosTenantSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public SophosTenantSearchSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string Kind => SearchSourceKind.SophosTenants;

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Feature-flag gate: a user without contracts_enabled gets zero hits,
        // exactly like the Microsoft 365 matching page is hidden from them.
        var enabled = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT COALESCE(contracts_enabled, FALSE) FROM users WHERE id = @userId",
            new { userId = principal.UserId },
            cancellationToken: ct));
        if (!enabled)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        const string sql = """
            WITH hits AS (
                SELECT  t.company_id,
                        t.tenant_id,
                        t.show_as,
                        t.company_code,
                        t.mailbox_count,
                        co.name AS company_name,
                        CASE
                            WHEN t.show_as      ILIKE @prefix THEN 4.0
                            WHEN t.company_code ILIKE @prefix THEN 3.5
                            WHEN t.show_as      ILIKE @like   THEN 2.0
                            WHEN t.name         ILIKE @like   THEN 1.5
                            WHEN t.company_code ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM sophos_tenants t
                  JOIN companies co ON co.id = t.company_id
                 WHERE t.company_id IS NOT NULL
                   AND (t.show_as      ILIKE @like
                     OR t.name         ILIKE @like
                     OR t.company_code ILIKE @like)
            )
            SELECT  company_id    AS CompanyId,
                    tenant_id     AS TenantId,
                    show_as       AS ShowAs,
                    company_code  AS CompanyCode,
                    mailbox_count AS MailboxCount,
                    company_name  AS CompanyName,
                    rank::double precision AS Rank,
                    total_hits    AS TotalHits
              FROM hits
             ORDER BY rank DESC, show_as ASC NULLS LAST
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<TenantHitRow>(new CommandDefinition(
            sql,
            new
            {
                prefix = EscapeLike(normalized) + "%",
                like = "%" + EscapeLike(normalized) + "%",
                limit,
                offset,
            },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(BuildHit).ToList();
        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private SearchHit BuildHit(TenantHitRow r)
    {
        var title = string.IsNullOrWhiteSpace(r.CompanyName) ? (r.ShowAs ?? "Sophos tenant") : r.CompanyName;
        var snippet = $"{r.MailboxCount} protected mailbox{(r.MailboxCount == 1 ? "" : "es")}";

        return new SearchHit(
            Kind: Kind,
            // Link to the owning company's Microsoft 365 detail page.
            EntityId: r.CompanyId.ToString(),
            Title: title!,
            Snippet: snippet,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["companyId"] = r.CompanyId.ToString(),
                ["companyName"] = r.CompanyName,
                ["companyCode"] = r.CompanyCode,
                ["showAs"] = r.ShowAs,
            });
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class TenantHitRow
    {
        public Guid CompanyId { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string? ShowAs { get; set; }
        public string? CompanyCode { get; set; }
        public int MailboxCount { get; set; }
        public string? CompanyName { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
