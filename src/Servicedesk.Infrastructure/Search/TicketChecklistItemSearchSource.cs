using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.103 — global-search source for checklist items attached to tickets.
/// A hit is "this ticket has a checklist step matching your words" and
/// routes to the ticket. Row-level authorization mirrors the ticket source:
/// agents see items on tickets in their accessible queues only, admins see
/// everything, customers nothing (checklists are internal). Re-checked
/// inside <see cref="SearchAsync"/> so a bypass of the façade still yields
/// zero hits without touching the database.
public sealed class TicketChecklistItemSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketChecklistItemSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.ChecklistItems;

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
            SearchSort.Newest => "t.updated_utc DESC, i.id DESC",
            SearchSort.Oldest => "t.updated_utc ASC, i.id ASC",
            _ => "rank DESC, t.updated_utc DESC, i.id DESC",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm)
            SELECT i.id               AS ItemId,
                   i.title            AS Title,
                   i.state            AS State,
                   c.id               AS ChecklistId,
                   c.name             AS ChecklistName,
                   t.id               AS TicketId,
                   t.number           AS TicketNumber,
                   t.subject          AS TicketSubject,
                   GREATEST(
                       similarity(lower(i.title), (SELECT norm FROM q)),
                       CASE WHEN lower(i.title) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.4 ELSE 0 END,
                       CASE WHEN lower(i.description) LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.2 ELSE 0 END
                   )::double precision AS Rank,
                   COUNT(*) OVER ()   AS TotalHits
            FROM ticket_checklist_items i
            JOIN ticket_checklists c ON c.id = i.checklist_id
            JOIN tickets t ON t.id = c.ticket_id
            WHERE t.is_deleted = FALSE
              AND (@skipQueueFilter OR t.queue_id = ANY(@allowedQueues))
              AND (
                     lower(i.title) % (SELECT norm FROM q)
                  OR lower(i.title) LIKE '%' || (SELECT norm FROM q) || '%'
                  OR lower(i.description) LIKE '%' || (SELECT norm FROM q) || '%'
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
            Title: r.Title,
            Snippet: $"#{r.TicketNumber} — {r.TicketSubject} · {r.ChecklistName}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["ticketId"] = r.TicketId.ToString(),
                ["ticketNumber"] = r.TicketNumber.ToString(),
                ["checklistId"] = r.ChecklistId.ToString(),
                ["checklistName"] = r.ChecklistName,
                ["itemId"] = r.ItemId.ToString(),
                ["state"] = r.State,
            })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private sealed class Row
    {
        public Guid ItemId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public Guid ChecklistId { get; set; }
        public string ChecklistName { get; set; } = string.Empty;
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
