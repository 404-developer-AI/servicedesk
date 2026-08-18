using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.103 — global-search source for the admin checklist-template
/// catalogue (name, description, item titles via the flattened
/// <c>search_text</c>). Admin-only: templates are configuration, and the
/// hit routes into Settings. Re-checked inside <see cref="SearchAsync"/>.
public sealed class ChecklistTemplateSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public ChecklistTemplateSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.ChecklistTemplates;

    public bool IsAvailableFor(SearchPrincipal principal) => principal.IsAdmin;

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "updated_utc DESC, id DESC",
            SearchSort.Oldest => "updated_utc ASC, id ASC",
            _ => "rank DESC, lower(name)",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm),
            hits AS (
                SELECT id, name, is_active, item_count, updated_utc,
                       GREATEST(
                           similarity(lower(name), (SELECT norm FROM q)),
                           CASE WHEN lower(name) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.4 ELSE 0 END,
                           CASE WHEN lower(search_text) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.2 ELSE 0 END
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM checklist_templates
                WHERE (
                       lower(name) % (SELECT norm FROM q)
                    OR lower(name) LIKE '%' || (SELECT norm FROM q) || '%'
                    OR lower(search_text) LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT id            AS Id,
                   name          AS Name,
                   is_active     AS IsActive,
                   item_count    AS ItemCount,
                   rank::double precision AS Rank,
                   total_hits    AS TotalHits
            FROM hits
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Row>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Name,
            Snippet: $"{r.ItemCount} item{(r.ItemCount == 1 ? "" : "s")}{(r.IsActive ? "" : " · inactive")}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["isActive"] = r.IsActive ? "true" : "false",
                ["itemCount"] = r.ItemCount.ToString(),
            })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int ItemCount { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
