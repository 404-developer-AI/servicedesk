using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Contact typeahead via pg_trgm similarity on email + full name. Not
/// exposed to Customer in v1 (the customer portal lands together with the
/// Companies/Users feature, which will re-scope this source).
public sealed class ContactSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public ContactSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Contacts;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // similarity() returns 0..1; 0.25 is a balanced cut-off for
        // typeahead — strict enough to hide noise, lax enough to forgive
        // one typo.
        const string sql = """
            WITH q AS (
                SELECT lower(@query) AS norm
            ),
            hits AS (
                SELECT c.id, c.first_name, c.last_name, c.email,
                       cc.company_id AS company_id,
                       GREATEST(
                           similarity(lower(c.email::text), (SELECT norm FROM q)),
                           similarity(lower(coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')),
                                      (SELECT norm FROM q))
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM contacts c
                LEFT JOIN contact_companies cc ON cc.contact_id = c.id AND cc.role = 'primary'
                WHERE c.is_active = TRUE
                  AND (
                        lower(c.email::text) % (SELECT norm FROM q)
                     OR lower(coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')) % (SELECT norm FROM q)
                     OR lower(c.email::text) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT id,
                   first_name  AS "FirstName",
                   last_name   AS "LastName",
                   email,
                   company_id  AS "CompanyId",
                   rank::double precision AS "Rank",
                   total_hits  AS "TotalHits"
            FROM hits
            ORDER BY rank DESC, last_name, first_name
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ContactHitRow>(new CommandDefinition(sql,
            new { query = normalized, limit, offset },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(r =>
        {
            var fullName = $"{r.FirstName} {r.LastName}".Trim();
            var title = string.IsNullOrWhiteSpace(fullName) ? r.Email : $"{fullName} — {r.Email}";
            return new SearchHit(
                Kind: Kind,
                EntityId: r.Id.ToString(),
                Title: title,
                Snippet: null,
                Rank: r.Rank,
                Meta: new Dictionary<string, string?>
                {
                    ["email"] = r.Email,
                    ["companyId"] = r.CompanyId?.ToString(),
                });
        }).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed record ContactHitRow(
        Guid Id,
        string FirstName,
        string LastName,
        string Email,
        Guid? CompanyId,
        double Rank,
        long TotalHits);
}
