using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Portal;

/// v0.1.0 — global-search source for customer-portal accounts (pending
/// registrations, active / deactivated / rejected accounts). Agents and
/// admins only: a customer has no global search at all, and the rule is
/// re-checked inside <see cref="SearchAsync"/> so a caller bypassing the
/// façade still gets zero hits without touching the database.
public sealed class PortalAccountSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public PortalAccountSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.PortalAccounts;

    public bool IsAvailableFor(SearchPrincipal principal) => principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // orderBy is a fixed switch over SearchSort — never user input.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "created_utc DESC, user_id DESC",
            SearchSort.Oldest => "created_utc ASC, user_id ASC",
            _ => "rank DESC, email",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm),
            hits AS (
                SELECT pa.user_id, u.email::text AS email, pa.display_name, pa.status, u.contact_id,
                       co.name AS company_name, pa.created_utc,
                       GREATEST(
                           similarity(lower(u.email::text), (SELECT norm FROM q)),
                           similarity(lower(pa.display_name), (SELECT norm FROM q)),
                           CASE WHEN lower(u.email::text) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.4 ELSE 0 END,
                           CASE WHEN lower(pa.display_name) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.3 ELSE 0 END
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM portal_accounts pa
                JOIN users u ON u.id = pa.user_id
                LEFT JOIN contacts c ON c.id = u.contact_id
                LEFT JOIN contact_companies cc ON cc.contact_id = c.id AND cc.role = 'primary'
                LEFT JOIN companies co ON co.id = cc.company_id
                WHERE (
                       lower(u.email::text) % (SELECT norm FROM q)
                    OR lower(u.email::text) LIKE '%' || (SELECT norm FROM q) || '%'
                    OR lower(pa.display_name) % (SELECT norm FROM q)
                    OR lower(pa.display_name) LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT user_id       AS UserId,
                   email         AS Email,
                   display_name  AS DisplayName,
                   status        AS Status,
                   contact_id    AS ContactId,
                   company_name  AS CompanyName,
                   created_utc   AS CreatedUtc,
                   rank::double precision AS Rank,
                   total_hits    AS TotalHits
            FROM hits
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Hit>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.UserId.ToString(),
            Title: string.IsNullOrWhiteSpace(r.DisplayName) ? r.Email : $"{r.DisplayName} <{r.Email}>",
            Snippet: r.CompanyName is null ? $"Portal account · {r.Status}" : $"Portal account · {r.Status} · {r.CompanyName}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["status"] = r.Status,
                ["contactId"] = r.ContactId?.ToString(),
                ["email"] = r.Email,
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed class Hit
    {
        public Guid UserId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid? ContactId { get; set; }
        public string? CompanyName { get; set; }
        public DateTime CreatedUtc { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
