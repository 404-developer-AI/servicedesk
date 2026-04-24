using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Admin-only lookup for intake templates so a global search for the
/// template name surfaces the Settings editor entry. Agent + Customer get
/// zero hits because the template catalogue is an admin concern (agents
/// pick a template via the `::`-trigger in the mail composer, not via
/// global search).
public sealed class IntakeTemplateSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public IntakeTemplateSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.IntakeTemplates;

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

        // Trigram similarity on name + description, with substring match
        // as a fallback for short queries that fall below the 0.25 trigram
        // cutoff. Same shape as ContactSearchSource.
        const string sql = """
            WITH q AS (SELECT lower(@query) AS norm),
            hits AS (
                SELECT id, name, description, is_active,
                       GREATEST(
                           similarity(lower(name), (SELECT norm FROM q)),
                           similarity(lower(coalesce(description, '')), (SELECT norm FROM q))
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM intake_templates
                WHERE is_active = TRUE
                  AND (
                       lower(name) % (SELECT norm FROM q)
                    OR lower(coalesce(description, '')) % (SELECT norm FROM q)
                    OR lower(name) LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT id, name, description, is_active AS "IsActive",
                   rank::double precision AS "Rank",
                   total_hits AS "TotalHits"
            FROM hits
            ORDER BY rank DESC, name
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TemplateHit>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Name,
            Snippet: r.Description,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["isActive"] = r.IsActive ? "true" : "false",
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed record TemplateHit(Guid Id, string Name, string? Description, bool IsActive, double Rank, long TotalHits);
}
