using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.1.67 (phase 3) — global-search source for maintenance plans: a hit is a
/// plan whose name or notes match, or that has a member company that matches;
/// it opens the planner with the plan. Same access as the planner itself: agents and admins with the
/// Campaigns flag (admins included). Plans carry no ticket content; the cycle
/// tickets stay queue-scoped on the board. Re-checked inside
/// <see cref="SearchAsync"/> so a bypass still yields zero hits without SQL.
public sealed class MaintenancePlanSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public MaintenancePlanSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.MaintenancePlans;

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
            SearchSort.Newest => "p.updated_utc DESC, p.id DESC",
            SearchSort.Oldest => "p.updated_utc ASC, p.id ASC",
            _ => "rank DESC, p.next_intervention_date NULLS LAST, p.id",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm,
                              '%' || replace(replace(replace(lower(@query), '\', '\\'), '%', '\%'), '_', '\_') || '%' AS pat)
            SELECT p.id AS Id, p.name AS Name,
                   (SELECT COUNT(*) FROM maintenance_plan_members m WHERE m.plan_id = p.id AND m.is_active)::int AS MemberCount,
                   p.next_intervention_date AS NextDate,
                   GREATEST(
                       similarity(p.search_text, (SELECT norm FROM q)),
                       CASE WHEN p.search_text LIKE (SELECT pat FROM q) THEN 0.5 ELSE 0 END,
                       CASE WHEN EXISTS (SELECT 1 FROM maintenance_plan_members m JOIN companies co ON co.id = m.company_id
                                         WHERE m.plan_id = p.id AND lower(co.name) LIKE (SELECT pat FROM q)) THEN 0.4 ELSE 0 END,
                       CASE WHEN EXISTS (SELECT 1 FROM maintenance_plan_members m JOIN maintenance_member_notes n ON n.member_id = m.id
                                         WHERE m.plan_id = p.id AND NOT n.is_deleted AND n.search_text LIKE (SELECT pat FROM q)) THEN 0.35 ELSE 0 END
                   )::double precision AS Rank,
                   COUNT(*) OVER () AS TotalHits
            FROM maintenance_plans p
            WHERE p.search_text % (SELECT norm FROM q)
               OR p.search_text LIKE (SELECT pat FROM q)
               OR EXISTS (SELECT 1 FROM maintenance_plan_members m JOIN companies co ON co.id = m.company_id
                          WHERE m.plan_id = p.id AND lower(co.name) LIKE (SELECT pat FROM q))
               -- v0.1.67 — the members' maintenance notes (owner decision: searchable).
               OR EXISTS (SELECT 1 FROM maintenance_plan_members m JOIN maintenance_member_notes n ON n.member_id = m.id
                          WHERE m.plan_id = p.id AND NOT n.is_deleted AND n.search_text LIKE (SELECT pat FROM q))
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Row>(new CommandDefinition(sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Name,
            Snippet: $"{r.MemberCount} customer{(r.MemberCount == 1 ? "" : "s")} · "
                     + (r.NextDate is { } d ? $"next intervention {d:yyyy-MM-dd}" : "no next intervention"),
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>())).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int MemberCount { get; set; }
        public DateTime? NextDate { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
