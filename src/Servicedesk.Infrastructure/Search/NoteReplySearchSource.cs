using Servicedesk.Infrastructure.Access;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.1.54 — global-search source for replies on internal notes. A hit is
/// "a reply on this ticket matches your words" and routes to the ticket with
/// the reply in view. Row-level authorization mirrors the ticket source:
/// agents see replies on tickets in their accessible queues only, admins
/// everything, customers nothing (replies are internal). Re-checked inside
/// <see cref="SearchAsync"/> so a bypass of the façade still yields zero hits
/// without touching the database.
public sealed class NoteReplySearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public NoteReplySearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.NoteReplies;

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
            SearchSort.Newest => "r.created_utc DESC, r.id DESC",
            SearchSort.Oldest => "r.created_utc ASC, r.id ASC",
            _ => "rank DESC, r.created_utc DESC, r.id DESC",
        };

        var sql = $"""
            WITH q AS (SELECT lower(@query) AS norm,
                              '%' || replace(replace(replace(lower(@query), '\', '\\'), '%', '\%'), '_', '\_') || '%' AS pat)
            SELECT r.id               AS ReplyId,
                   r.body_text        AS BodyText,
                   u.email::text      AS AuthorEmail,
                   t.id               AS TicketId,
                   t.number           AS TicketNumber,
                   t.subject          AS TicketSubject,
                   GREATEST(
                       similarity(lower(r.body_text), (SELECT norm FROM q)),
                       CASE WHEN lower(r.body_text) LIKE (SELECT pat FROM q) THEN 0.4 ELSE 0 END
                   )::double precision AS Rank,
                   COUNT(*) OVER ()   AS TotalHits
            FROM ticket_note_replies r
            JOIN ticket_events e ON e.id = r.root_event_id
            JOIN tickets t ON t.id = e.ticket_id
            LEFT JOIN users u ON u.id = r.author_user_id
            WHERE r.deleted_utc IS NULL
              AND t.is_deleted = FALSE
              AND {QueueAccessScope.For(principal).SqlPredicate("t.queue_id")}
              AND lower(r.body_text) LIKE (SELECT pat FROM q)
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            query = normalized,
            AccessibleQueueIds = allowedQueues?.ToArray() ?? Array.Empty<Guid>(),
            limit,
            offset,
        }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.TicketId.ToString(),
            Title: r.BodyText.Length > 120 ? r.BodyText[..119] + "…" : r.BodyText,
            Snippet: $"#{r.TicketNumber} — {r.TicketSubject} · reply by {r.AuthorEmail ?? "unknown"}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["ticketId"] = r.TicketId.ToString(),
                ["ticketNumber"] = r.TicketNumber.ToString(),
                ["replyId"] = r.ReplyId.ToString(),
            })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private sealed class Row
    {
        public long ReplyId { get; set; }
        public string BodyText { get; set; } = string.Empty;
        public string? AuthorEmail { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
