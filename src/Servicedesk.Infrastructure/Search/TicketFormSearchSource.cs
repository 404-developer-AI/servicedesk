using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.1.52 — global-search source for filled forms on tickets. A hit is
/// "this ticket has a form whose name or answers match your words" and
/// routes to the ticket with the form opened. Row-level authorization
/// mirrors the ticket source: agents see forms on tickets in their
/// accessible queues only, admins everything, customers nothing (forms are
/// internal). Re-checked inside <see cref="SearchAsync"/> so a bypass of the
/// façade still yields zero hits without touching the database.
public sealed class TicketFormSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketFormSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.TicketForms;

    public bool IsAvailableFor(SearchPrincipal principal) => principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var allowedQueues = principal.AllowedQueueIds;
        if (!principal.IsAdmin && (allowedQueues is null || allowedQueues.Count == 0))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Fixed switch over SearchSort — never user input.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "f.updated_utc DESC, f.id DESC",
            SearchSort.Oldest => "f.updated_utc ASC, f.id ASC",
            _ => "rank DESC, f.updated_utc DESC, f.id DESC",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm,
                              '%' || replace(replace(replace(lower(@query), '\', '\'), '%', '\%'), '_', '\_') || '%' AS pat)
            SELECT f.id               AS FormId,
                   f.name             AS Name,
                   f.version          AS Version,
                   t.id               AS TicketId,
                   t.number           AS TicketNumber,
                   t.subject          AS TicketSubject,
                   GREATEST(
                       similarity(lower(f.name), (SELECT norm FROM q)),
                       CASE WHEN lower(f.name) LIKE (SELECT pat FROM q) THEN 0.5 ELSE 0 END,
                       CASE WHEN lower(f.search_text) LIKE (SELECT pat FROM q) THEN 0.3 ELSE 0 END
                   )::double precision AS Rank,
                   COUNT(*) OVER ()   AS TotalHits
            FROM ticket_forms f
            JOIN tickets t ON t.id = f.ticket_id
            WHERE f.deleted_utc IS NULL
              AND t.is_deleted = FALSE
              AND (@skipQueueFilter OR t.queue_id = ANY(@allowedQueues))
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
            skipQueueFilter = principal.IsAdmin,
            allowedQueues = allowedQueues?.ToArray() ?? Array.Empty<Guid>(),
            limit,
            offset,
        }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.TicketId.ToString(),
            Title: r.Name,
            Snippet: $"#{r.TicketNumber} — {r.TicketSubject} · v{r.Version}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["ticketId"] = r.TicketId.ToString(),
                ["ticketNumber"] = r.TicketNumber.ToString(),
                ["formId"] = r.FormId.ToString(),
                ["version"] = r.Version.ToString(),
            })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private sealed class Row
    {
        public Guid FormId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Version { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
