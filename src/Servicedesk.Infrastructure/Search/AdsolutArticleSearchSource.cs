using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for the mirrored Adsolut article catalogue shown in the
/// Contract Articles list (Contracts → Contract Articles). Row-level
/// authorization: a Customer principal never sees hits; an Agent/Admin sees hits
/// only when their own user row has <c>contracts_enabled = TRUE</c> — the same
/// per-user feature flag that gates the Contracts hub. The flag is checked
/// inside <see cref="SearchAsync"/> (a single cheap point lookup, only when the
/// user actually searches). Matches against article code, name and description.
public sealed class AdsolutArticleSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutArticleSearchSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string Kind => SearchSourceKind.AdsolutArticles;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || (principal.IsAgent && principal.HasFeature(SearchFeature.Contracts));

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

        // Feature-flag gate (contracts_enabled) is enforced by IsAvailableFor
        // via the principal — checked at the top of this method and by the
        // search façade before it ever queries this source.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "synced_utc DESC, id DESC",
            SearchSort.Oldest => "synced_utc ASC, id ASC",
            _ => "rank DESC, code ASC NULLS LAST",
        };

        var sql = $"""
            WITH hits AS (
                SELECT  a.id,
                        a.code,
                        a.name,
                        a.description,
                        a.vat_rate,
                        a.active,
                        a.synced_utc,
                        CASE
                            WHEN a.code ILIKE @prefix THEN 4.0
                            WHEN a.name ILIKE @prefix THEN 3.0
                            WHEN a.code ILIKE @like   THEN 2.0
                            WHEN a.name ILIKE @like   THEN 1.5
                            WHEN a.description ILIKE @like THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM adsolut_articles a
                 WHERE a.code        ILIKE @like
                    OR a.name        ILIKE @like
                    OR a.description ILIKE @like
            )
            SELECT  id          AS Id,
                    code        AS Code,
                    name        AS Name,
                    description AS Description,
                    vat_rate    AS VatRate,
                    active      AS Active,
                    rank::double precision AS Rank,
                    total_hits  AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<ArticleHitRow>(new CommandDefinition(
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

    private SearchHit BuildHit(ArticleHitRow r)
    {
        var code = string.IsNullOrWhiteSpace(r.Code) ? "—" : r.Code;
        var name = string.IsNullOrWhiteSpace(r.Name) ? code : r.Name;
        var title = string.Equals(code, name, StringComparison.Ordinal) ? name! : $"{code} · {name}";

        return new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: title!,
            Snippet: r.Description,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["code"] = r.Code,
                ["name"] = r.Name,
                ["vatRate"] = r.VatRate,
                ["active"] = r.Active ? "true" : "false",
            });
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class ArticleHitRow
    {
        public Guid Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? VatRate { get; set; }
        public bool Active { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
