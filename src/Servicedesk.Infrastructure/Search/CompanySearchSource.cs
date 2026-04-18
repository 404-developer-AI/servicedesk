using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.9: companies typeahead via pg_trgm similarity on name, short_name,
/// code and vat_number. Customers never see results (the customer portal
/// does not surface company directories); agents and admins do. Inactive
/// companies are filtered out.
public sealed class CompanySearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public CompanySearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Companies;

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

        // Trigram similarity across four searchable fields. The % operator
        // uses the GIN trigram indexes; LIKE substring match is a safety net
        // so short / exact-code queries always hit even if similarity falls
        // below the default threshold.
        const string sql = """
            WITH q AS (
                SELECT lower(@query) AS norm
            ),
            hits AS (
                SELECT c.id, c.name, c.short_name, c.code::text AS code, c.vat_number,
                       GREATEST(
                           similarity(lower(coalesce(c.name, '')),        (SELECT norm FROM q)),
                           similarity(lower(coalesce(c.short_name, '')),  (SELECT norm FROM q)),
                           similarity(lower(c.code::text),                (SELECT norm FROM q)),
                           similarity(lower(coalesce(c.vat_number, '')),  (SELECT norm FROM q))
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM companies c
                WHERE c.is_active = TRUE
                  AND (
                        lower(coalesce(c.name, ''))        % (SELECT norm FROM q)
                     OR lower(coalesce(c.short_name, ''))  % (SELECT norm FROM q)
                     OR lower(c.code::text)                % (SELECT norm FROM q)
                     OR lower(coalesce(c.vat_number, ''))  % (SELECT norm FROM q)
                     OR lower(coalesce(c.name, ''))        LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(c.short_name, ''))  LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(c.code::text)                LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(c.vat_number, ''))  LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT id,
                   name,
                   short_name  AS "ShortName",
                   code,
                   vat_number  AS "VatNumber",
                   rank::double precision AS "Rank",
                   total_hits  AS "TotalHits"
            FROM hits
            ORDER BY rank DESC, name
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<CompanyHitRow>(new CommandDefinition(sql,
            new { query = normalized, limit, offset },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(r =>
        {
            var title = string.IsNullOrWhiteSpace(r.ShortName)
                ? $"{r.Name} ({r.Code})"
                : $"{r.Name} — {r.ShortName} ({r.Code})";
            return new SearchHit(
                Kind: Kind,
                EntityId: r.Id.ToString(),
                Title: title,
                Snippet: string.IsNullOrWhiteSpace(r.VatNumber) ? null : r.VatNumber,
                Rank: r.Rank,
                Meta: new Dictionary<string, string?>
                {
                    ["code"] = r.Code,
                    ["shortName"] = r.ShortName,
                    ["vatNumber"] = r.VatNumber,
                });
        }).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed record CompanyHitRow(
        Guid Id,
        string Name,
        string ShortName,
        string Code,
        string VatNumber,
        double Rank,
        long TotalHits);
}
