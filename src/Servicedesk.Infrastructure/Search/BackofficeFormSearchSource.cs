using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.1.54 — global-search source for the Back office forms work list. A hit
/// is a form submitted to back office whose name or answers match; it routes
/// to /backoffice-forms with that form opened. Available only to users with
/// the per-user Back office forms flag — admins included, same as the API —
/// and, like the work list, not queue-scoped (owner decision). Re-checked
/// inside <see cref="SearchAsync"/> so a bypass of the façade still yields
/// zero hits without touching the database.
public sealed class BackofficeFormSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public BackofficeFormSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.BackofficeForms;

    public bool IsAvailableFor(SearchPrincipal principal)
        => (principal.IsAdmin || principal.IsAgent) && principal.HasFeature(SearchFeature.BackofficeForms);

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
            SearchSort.Newest => "f.bo_submitted_utc DESC NULLS LAST, f.id DESC",
            SearchSort.Oldest => "f.bo_submitted_utc ASC NULLS LAST, f.id ASC",
            _ => "rank DESC, f.bo_submitted_utc DESC NULLS LAST, f.id DESC",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm,
                              '%' || replace(replace(replace(lower(@query), '\', '\'), '%', '\%'), '_', '\_') || '%' AS pat)
            SELECT f.id         AS FormId,
                   f.name       AS Name,
                   f.bo_status  AS Status,
                   t.number     AS TicketNumber,
                   t.subject    AS TicketSubject,
                   GREATEST(
                       similarity(lower(f.name), (SELECT norm FROM q)),
                       CASE WHEN lower(f.name) LIKE (SELECT pat FROM q) THEN 0.5 ELSE 0 END,
                       CASE WHEN lower(f.search_text) LIKE (SELECT pat FROM q) THEN 0.3 ELSE 0 END
                   )::double precision AS Rank,
                   COUNT(*) OVER () AS TotalHits
            FROM ticket_forms f
            JOIN tickets t ON t.id = f.ticket_id
            WHERE f.bo_status IS NOT NULL
              AND f.deleted_utc IS NULL
              AND t.is_deleted = FALSE
              AND (
                     lower(f.name) % (SELECT norm FROM q)
                  OR lower(f.name) LIKE (SELECT pat FROM q)
                  OR lower(f.search_text) LIKE (SELECT pat FROM q)
              )
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
            EntityId: r.FormId.ToString(),
            Title: r.Name,
            Snippet: $"{StatusLabel(r.Status)} · #{r.TicketNumber} — {r.TicketSubject}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["status"] = r.Status,
                ["ticketNumber"] = r.TicketNumber.ToString(),
            })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private static string StatusLabel(string status) => status switch
    {
        "InProgress" => "In progress",
        _ => status,
    };

    private sealed class Row
    {
        public Guid FormId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
