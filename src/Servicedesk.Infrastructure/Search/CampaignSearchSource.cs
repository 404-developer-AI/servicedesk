using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.1.67 — global-search source for Campaigns. A hit is a campaign whose
/// name or subject matches; it routes to the campaign page. Available only
/// to agents and admins with the per-user Campaigns flag (admins included,
/// same as the API). Campaign metadata is shared among flag holders like the
/// back-office work list; the recipient rows behind it stay queue-scoped on
/// the page itself. Re-checked inside <see cref="SearchAsync"/> so a bypass
/// of the façade still yields zero hits without touching the database.
public sealed class CampaignSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public CampaignSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Campaigns;

    public bool IsAvailableFor(SearchPrincipal principal)
        => (principal.IsAdmin || principal.IsAgent) && principal.HasFeature(SearchFeature.Campaigns);

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Fixed switch over SearchSort — never user input.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "c.updated_utc DESC, c.id DESC",
            SearchSort.Oldest => "c.updated_utc ASC, c.id ASC",
            _ => "rank DESC, c.updated_utc DESC, c.id DESC",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm,
                              '%' || replace(replace(replace(lower(@query), '\', '\\'), '%', '\%'), '_', '\_') || '%' AS pat)
            SELECT c.id        AS Id,
                   c.name      AS Name,
                   c.subject   AS Subject,
                   c.state     AS State,
                   GREATEST(
                       similarity(c.search_text, (SELECT norm FROM q)),
                       CASE WHEN c.search_text LIKE (SELECT pat FROM q) THEN 0.5 ELSE 0 END
                   )::double precision AS Rank,
                   COUNT(*) OVER () AS TotalHits
            FROM campaigns c
            WHERE c.search_text % (SELECT norm FROM q)
               OR c.search_text LIKE (SELECT pat FROM q)
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            query = normalized,
            limit,
            offset,
        }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Name,
            Snippet: string.IsNullOrWhiteSpace(r.Subject) ? r.State : $"{r.State} · {r.Subject}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?> { ["state"] = r.State })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
