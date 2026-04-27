using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.24 Blok 8 — global-search source for the admin trigger catalogue.
/// Admin-only: agents and customers never see triggers because triggers
/// hold privileged automation logic (auto-reply, set-owner, escalation
/// routing) that isn't a customer-facing concept. The hard rule lives in
/// <see cref="IsAvailableFor"/> and is re-checked inside
/// <see cref="SearchAsync"/> as belt-and-braces: if a caller bypasses the
/// façade's filtering, this source still returns zero hits without
/// touching the database.
public sealed class TriggerSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public TriggerSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Triggers;

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

        // Same trigram + ILIKE-fallback shape as IntakeTemplateSearchSource:
        // trigram catches typos / fuzzy matches, the literal substring
        // fallback rescues short queries below the 0.25 trigram cutoff.
        const string sql = """
            WITH q AS (SELECT lower(@query) AS norm),
            hits AS (
                SELECT id, name, description, is_active, activator_kind, activator_mode,
                       GREATEST(
                           similarity(lower(name), (SELECT norm FROM q)),
                           similarity(lower(coalesce(description, '')), (SELECT norm FROM q))
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM triggers
                WHERE (
                       lower(name) % (SELECT norm FROM q)
                    OR lower(coalesce(description, '')) % (SELECT norm FROM q)
                    OR lower(name) LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT id                              AS Id,
                   name                            AS Name,
                   description                     AS Description,
                   is_active                       AS IsActive,
                   activator_kind                  AS ActivatorKind,
                   activator_mode                  AS ActivatorMode,
                   rank::double precision          AS Rank,
                   total_hits                      AS TotalHits
            FROM hits
            ORDER BY rank DESC, name
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TriggerHit>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Name,
            Snippet: string.IsNullOrWhiteSpace(r.Description) ? null : r.Description,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["isActive"] = r.IsActive ? "true" : "false",
                ["activatorKind"] = r.ActivatorKind,
                ["activatorMode"] = r.ActivatorMode,
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed class TriggerHit
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public string ActivatorKind { get; set; } = string.Empty;
        public string ActivatorMode { get; set; } = string.Empty;
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
