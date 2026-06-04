using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.58 — global-search source for the admin email-signature catalogue.
/// Admin-only: signatures carry company branding + the system-mail identity,
/// neither of which is a customer- or agent-facing concept. The rule lives in
/// <see cref="IsAvailableFor"/> and is re-checked inside
/// <see cref="SearchAsync"/> as belt-and-braces so a caller that bypasses the
/// façade's filtering still gets zero hits without touching the database.
public sealed class SignatureSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public SignatureSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Signatures;

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

        const string sql = """
            WITH q AS (SELECT lower(@query) AS norm),
            hits AS (
                SELECT id, name, is_system, enabled,
                       GREATEST(
                           similarity(lower(name), (SELECT norm FROM q)),
                           CASE WHEN lower(name) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.3 ELSE 0 END
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM mail_signatures
                WHERE (
                       lower(name) % (SELECT norm FROM q)
                    OR lower(name) LIKE '%' || (SELECT norm FROM q) || '%'
                  )
            )
            SELECT id              AS Id,
                   name            AS Name,
                   is_system       AS IsSystem,
                   enabled         AS Enabled,
                   rank::double precision AS Rank,
                   total_hits      AS TotalHits
            FROM hits
            ORDER BY rank DESC, name
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<SignatureHit>(new CommandDefinition(
            sql, new { query = normalized, limit, offset }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: r.Name,
            Snippet: r.IsSystem ? "System signature" : null,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["isSystem"] = r.IsSystem ? "true" : "false",
                ["enabled"] = r.Enabled ? "true" : "false",
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed class SignatureHit
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsSystem { get; set; }
        public bool Enabled { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
