using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Admin-only global search hits for the surveys catalogue so a Cmd+K
/// search for the survey name surfaces the Settings designer entry. Agents
/// and customers get zero hits — the survey designer is admin-scope, and
/// customers fill in surveys via direct token URLs.
public sealed class SurveySearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public SurveySearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Surveys;

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

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "updated_utc DESC, id DESC",
            SearchSort.Oldest => "updated_utc ASC, id ASC",
            _ => "rank DESC, name",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm),
            hits AS (
                SELECT id, name, description, is_active, updated_utc,
                       GREATEST(
                           similarity(lower(name), (SELECT norm FROM q)),
                           similarity(lower(coalesce(description, '')), (SELECT norm FROM q))
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM surveys
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
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<SurveyHit>(new CommandDefinition(
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

    private sealed record SurveyHit(Guid Id, string Name, string? Description, bool IsActive, double Rank, long TotalHits);
}
