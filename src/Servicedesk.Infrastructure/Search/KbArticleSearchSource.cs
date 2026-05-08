using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.31: knowledge base article search. Backed by the per-translation
/// generated <c>search_vector</c> (title weight A + body_text weight B,
/// 'simple' config + lower) plus a pg_trgm safety net on title for
/// short / typo-tolerant queries.
///
/// Row-level rules:
///   Customer principal → zero hits (defensive — the public KB tier lands
///                                   in v0.1.x with its own auth path).
///   Agent/Admin       → only Internal + Published. Drafts and Archived
///                       are intentionally excluded from global search;
///                       agents reach drafts via the dedicated /kb listing.
public sealed class KbArticleSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public KbArticleSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.KbArticles;

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

        // Two match-paths combined:
        //   1) FTS: plainto_tsquery against the per-translation search_vector.
        //   2) Trigram: lower(title) % normalized — keeps short / fuzzy
        //      queries hitting even when the FTS query has no tokens.
        // ts_rank_cd weighs A-tokens (title) above B-tokens (body), and the
        // trigram-only branch lands at rank 0 so FTS hits sort first.
        // Sections-path is built via a chain join so the result includes a
        // human-readable breadcrumb regardless of nesting depth.
        const string sql = """
            WITH q AS (
                SELECT @query AS raw,
                       plainto_tsquery('simple', lower(@query)) AS tsq,
                       lower(@query) AS norm
            ),
            hits AS (
                SELECT a.id, a.section_id, a.slug, t.title, t.body_text,
                       CASE
                           WHEN t.search_vector @@ (SELECT tsq FROM q)
                               THEN ts_rank_cd(t.search_vector, (SELECT tsq FROM q))
                           ELSE 0
                       END AS fts_rank,
                       CASE
                           WHEN lower(t.title) % (SELECT norm FROM q)
                               THEN similarity(lower(t.title), (SELECT norm FROM q))
                           ELSE 0
                       END AS trgm_rank,
                       COUNT(*) OVER () AS total_hits
                  FROM kb_articles a
                  JOIN knowledge_base kb ON TRUE
                  LEFT JOIN kb_article_translations t
                       ON t.article_id = a.id AND t.locale_code = kb.default_locale_code
                 WHERE a.status IN ('Internal','Published')
                   AND t.id IS NOT NULL
                   AND (
                        t.search_vector @@ (SELECT tsq FROM q)
                     OR lower(t.title) % (SELECT norm FROM q)
                     OR lower(t.title) LIKE '%' || (SELECT norm FROM q) || '%'
                       )
            )
            SELECT id                 AS Id,
                   section_id         AS SectionId,
                   slug               AS Slug,
                   title              AS Title,
                   body_text          AS BodyText,
                   (fts_rank + trgm_rank)::double precision AS Rank,
                   total_hits         AS TotalHits
              FROM hits
             ORDER BY (fts_rank + trgm_rank) DESC, title
             LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<KbHitRow>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Title,
            Snippet: BuildSnippet(r.BodyText, normalized),
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["sectionId"] = r.SectionId.ToString(),
                ["slug"] = r.Slug,
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    /// Compact preview around the first match. Falls back to the leading
    /// 200 characters when the query token doesn't appear (trigram-only hit).
    private static string? BuildSnippet(string? bodyText, string query)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return null;
        var lower = bodyText.ToLowerInvariant();
        var idx = lower.IndexOf(query.ToLowerInvariant(), StringComparison.Ordinal);
        const int Window = 160;
        if (idx < 0)
        {
            return bodyText.Length <= Window ? bodyText.Trim() : bodyText[..Window].Trim() + "…";
        }
        var start = Math.Max(0, idx - Window / 4);
        var end = Math.Min(bodyText.Length, start + Window);
        var slice = bodyText[start..end].Trim();
        if (start > 0) slice = "…" + slice;
        if (end < bodyText.Length) slice += "…";
        return slice;
    }

    private sealed record KbHitRow(
        Guid Id,
        Guid SectionId,
        string Slug,
        string Title,
        string BodyText,
        double Rank,
        long TotalHits);
}
