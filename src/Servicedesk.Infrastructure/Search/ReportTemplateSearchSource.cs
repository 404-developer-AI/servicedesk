using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for the report email templates authored under the
/// Contracts → Settings module. Same row-level authorization as the other
/// Contracts sources: a Customer never sees hits; an Agent/Admin sees hits only
/// when their own user row has <c>contracts_enabled = TRUE</c>. Matches against
/// the template name + description; every hit links to the template editor.
public sealed class ReportTemplateSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public ReportTemplateSearchSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string Kind => SearchSourceKind.ReportTemplates;

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

        const string sql = """
            WITH hits AS (
                SELECT  id,
                        name,
                        description,
                        is_active,
                        CASE
                            WHEN name        ILIKE @prefix THEN 4.0
                            WHEN name        ILIKE @like   THEN 2.0
                            WHEN description ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM report_templates
                 WHERE name ILIKE @like OR description ILIKE @like
            )
            SELECT  id          AS Id,
                    name        AS Name,
                    description AS Description,
                    is_active   AS IsActive,
                    rank::double precision AS Rank,
                    total_hits  AS TotalHits
              FROM hits
             ORDER BY rank DESC, is_active DESC, lower(name)
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<TemplateHitRow>(new CommandDefinition(
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

    private SearchHit BuildHit(TemplateHitRow r) => new(
        Kind: Kind,
        EntityId: r.Id.ToString(),
        Title: r.Name,
        Snippet: r.IsActive ? r.Description : $"(inactive) {r.Description}".Trim(),
        Rank: r.Rank,
        Meta: new Dictionary<string, string?>
        {
            ["templateId"] = r.Id.ToString(),
            ["isActive"] = r.IsActive ? "true" : "false",
        });

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class TemplateHitRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
