using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// v0.1.33 — paged ticket timeline. `GET /api/tickets/{id}` used to return
/// every event of a ticket (avg 152 KB, p95 800 KB in the Performance
/// report); it now returns the latest page plus the few things the detail
/// page computed over *all* events (counts per type, the latest inbound mail
/// for the composer, pinned events, the Created event's metadata). Older
/// events come from <see cref="ListOlderAsync"/>; "::" order pills across the
/// whole timeline from <see cref="ListTaggedOrdersAsync"/> (a body scan —
/// kept off the ticket-open path, only the Orders feature asks for it).
///
/// Ordering is (created_utc, id) everywhere — merged-in events keep their
/// original created_utc, so id alone is not chronological. Event types in
/// <c>excludedTypes</c> (survey events for non-admins) are filtered in SQL so
/// page boundaries and counts match what the caller is allowed to see.
public interface ITicketTimelineReader
{
    Task<TicketTimelineFirstPage?> GetFirstPageAsync(
        Guid ticketId, int pageSize, IReadOnlyCollection<string> excludedTypes, CancellationToken ct);

    /// Events strictly older than <paramref name="beforeEventId"/>, newest
    /// <paramref name="limit"/> of them — or, with <paramref name="untilEventId"/>,
    /// every event back to and including that one (capped at
    /// <paramref name="limit"/>). Returned oldest first.
    Task<TicketTimelineSlice> ListOlderAsync(
        Guid ticketId, long beforeEventId, long? untilEventId, int limit,
        IReadOnlyCollection<string> excludedTypes, CancellationToken ct);

    Task<IReadOnlyList<TaggedOrderRef>> ListTaggedOrdersAsync(Guid ticketId, CancellationToken ct);
}

public sealed record TicketTimelineFirstPage(
    TicketDetail Detail,
    bool HasOlderEvents,
    IReadOnlyDictionary<string, int> EventTypeCounts,
    TicketEvent? LatestMailReceived,
    IReadOnlyList<TicketEvent> PinnedEventsOutsidePage,
    string? CreatedEventMetadataJson);

public sealed record TicketTimelineSlice(IReadOnlyList<TicketEvent> Events, bool HasMore);

public sealed record TaggedOrderRef(string Id, string Label);

public sealed class TicketTimelineReader : ITicketTimelineReader
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketTimelineReader(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string EventCols = """
        e.id AS Id, e.ticket_id AS TicketId, e.event_type AS EventType,
        e.author_user_id AS AuthorUserId, e.author_contact_id AS AuthorContactId,
        COALESCE(au.email, NULLIF(CONCAT_WS(' ', ac.first_name, ac.last_name), ''), e.metadata->>'authorName') AS AuthorName,
        e.body_text AS BodyText, e.body_html AS BodyHtml,
        e.metadata::text AS MetadataJson, e.is_internal AS IsInternal,
        e.created_utc AS CreatedUtc,
        e.edited_utc AS EditedUtc, e.edited_by_user_id AS EditedByUserId
        """;

    private const string EventJoins = """
        FROM ticket_events e
        LEFT JOIN users    au ON au.id = e.author_user_id
        LEFT JOIN contacts ac ON ac.id = e.author_contact_id
        """;

    // Same core columns as TicketRepository.GetByIdAsync.
    private const string TicketCoreSql = """
        SELECT id AS Id, number AS Number, subject AS Subject,
               requester_contact_id AS RequesterContactId, assignee_user_id AS AssigneeUserId,
               queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
               category_id AS CategoryId, source AS Source, external_ref AS ExternalRef,
               created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, due_utc AS DueUtc,
               first_response_utc AS FirstResponseUtc, resolved_utc AS ResolvedUtc,
               closed_utc AS ClosedUtc, is_deleted AS IsDeleted,
               company_id AS CompanyId,
               awaiting_company_assignment AS AwaitingCompanyAssignment,
               company_resolved_via AS CompanyResolvedVia,
               merged_into_ticket_id AS MergedIntoTicketId,
               merged_utc AS MergedUtc,
               merged_by_user_id AS MergedByUserId,
               split_from_ticket_id AS SplitFromTicketId,
               split_from_utc AS SplitFromUtc,
               split_from_user_id AS SplitFromUserId,
               pending_till_utc AS PendingTillUtc,
               pending_till_next_trigger_id AS PendingTillNextTriggerId,
               parent_ticket_id AS ParentTicketId,
               parent_linked_utc AS ParentLinkedUtc,
               parent_linked_by_user_id AS ParentLinkedByUserId,
               ticket_type_id AS TicketTypeId,
               zammad_ticket_id AS ZammadTicketId,
               zammad_ticket_number AS ZammadTicketNumber,
               is_project AS IsProject,
               project_ticket_id AS ProjectTicketId,
               project_linked_utc AS ProjectLinkedUtc,
               project_linked_by_user_id AS ProjectLinkedByUserId,
               project_sort_order AS ProjectSortOrder,
               project_prompt_dismissed_utc AS ProjectPromptDismissedUtc,
               is_callback AS IsCallback,
               is_research AS IsResearch
        FROM tickets WHERE id = @id AND is_deleted = FALSE
        """;

    public async Task<TicketTimelineFirstPage?> GetFirstPageAsync(
        Guid ticketId, int pageSize, IReadOnlyCollection<string> excludedTypes, CancellationToken ct)
    {
        // One batch, one round-trip — every statement is driven by
        // ix_ticket_events_ticket_created (ticket_id, created_utc DESC, id DESC).
        var sql = $"""
            {TicketCoreSql};

            SELECT ticket_id AS TicketId, body_text AS BodyText, body_html AS BodyHtml
            FROM ticket_bodies WHERE ticket_id = @id;

            -- latest page (+1 row to know whether older events exist);
            -- LIMIT NULL = all events (page size 0)
            SELECT {EventCols}
            {EventJoins}
            WHERE e.ticket_id = @id AND NOT (e.event_type = ANY(@excluded))
            ORDER BY e.created_utc DESC, e.id DESC
            LIMIT @limit;

            SELECT p.id AS Id, p.event_id AS EventId, p.ticket_id AS TicketId,
                   p.pinned_by_user_id AS PinnedByUserId,
                   u.email AS PinnedByName,
                   p.remark AS Remark,
                   p.created_utc AS CreatedUtc
            FROM ticket_event_pins p
            JOIN users u ON u.id = p.pinned_by_user_id
            WHERE p.ticket_id = @id
            ORDER BY p.created_utc;

            SELECT event_type AS EventType, COUNT(*)::int AS Count
            FROM ticket_events
            WHERE ticket_id = @id AND NOT (event_type = ANY(@excluded))
            GROUP BY event_type;

            -- the composer's reply / quote / recipient source
            SELECT {EventCols}
            {EventJoins}
            WHERE e.ticket_id = @id AND e.event_type = 'MailReceived'
            ORDER BY e.created_utc DESC, e.id DESC
            LIMIT 1;

            SELECT {EventCols}
            {EventJoins}
            WHERE e.ticket_id = @id
              AND e.id IN (SELECT event_id FROM ticket_event_pins WHERE ticket_id = @id)
              AND NOT (e.event_type = ANY(@excluded));

            SELECT metadata::text
            FROM ticket_events
            WHERE ticket_id = @id AND event_type = 'Created'
            ORDER BY created_utc, id
            LIMIT 1;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql, new
        {
            id = ticketId,
            excluded = excludedTypes.ToArray(),
            limit = pageSize > 0 ? pageSize + 1 : (int?)null,
        }, cancellationToken: ct));

        var ticket = await grid.ReadFirstOrDefaultAsync<Ticket>();
        var body = await grid.ReadFirstOrDefaultAsync<TicketBody>() ?? new TicketBody(ticketId, string.Empty, null);
        var newestFirst = (await grid.ReadAsync<TicketEvent>()).ToList();
        var pins = (await grid.ReadAsync<TicketEventPin>()).ToList();
        var counts = (await grid.ReadAsync<TypeCountRow>()).ToDictionary(r => r.EventType, r => r.Count, StringComparer.Ordinal);
        var latestMail = await grid.ReadFirstOrDefaultAsync<TicketEvent>();
        var pinnedEvents = (await grid.ReadAsync<TicketEvent>()).ToList();
        var createdMetadata = await grid.ReadFirstOrDefaultAsync<string?>();
        if (ticket is null) return null;

        var hasOlder = pageSize > 0 && newestFirst.Count > pageSize;
        if (hasOlder) newestFirst.RemoveAt(newestFirst.Count - 1);
        newestFirst.Reverse();
        var page = newestFirst;

        var pageIds = page.Select(e => e.Id).ToHashSet();
        return new TicketTimelineFirstPage(
            Detail: new TicketDetail(ticket, body, page, pins),
            HasOlderEvents: hasOlder,
            EventTypeCounts: counts,
            LatestMailReceived: latestMail is not null && pageIds.Contains(latestMail.Id) ? null : latestMail,
            PinnedEventsOutsidePage: pinnedEvents.Where(e => !pageIds.Contains(e.Id)).ToList(),
            CreatedEventMetadataJson: createdMetadata);
    }

    public async Task<IReadOnlyList<TaggedOrderRef>> ListTaggedOrdersAsync(Guid ticketId, CancellationToken ct)
    {
        // Pills are inserted by the composer, so only agent-written bodies can
        // carry one (an inbound mail never does) — skips the largest bodies.
        const string sql = """
            SELECT body_html FROM ticket_bodies
            WHERE ticket_id = @id AND body_html LIKE '%data-order-id%'
            UNION ALL
            (SELECT body_html
             FROM ticket_events
             WHERE ticket_id = @id
               AND event_type <> 'MailReceived'
               AND body_html LIKE '%data-order-id%'
             ORDER BY created_utc, id)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var bodies = await conn.QueryAsync<string?>(new CommandDefinition(sql, new { id = ticketId }, cancellationToken: ct));
        return ExtractTaggedOrders(bodies);
    }

    public async Task<TicketTimelineSlice> ListOlderAsync(
        Guid ticketId, long beforeEventId, long? untilEventId, int limit,
        IReadOnlyCollection<string> excludedTypes, CancellationToken ct)
    {
        var sql = $"""
            SELECT {EventCols}
            {EventJoins}
            WHERE e.ticket_id = @id
              AND NOT (e.event_type = ANY(@excluded))
              AND (e.created_utc, e.id) < (SELECT created_utc, id FROM ticket_events
                                            WHERE id = @before AND ticket_id = @id)
              AND (@until::bigint IS NULL
                   OR (e.created_utc, e.id) >= (SELECT created_utc, id FROM ticket_events
                                                 WHERE id = @until AND ticket_id = @id))
            ORDER BY e.created_utc DESC, e.id DESC
            LIMIT @limit;
            """;
        const string moreSql = """
            SELECT EXISTS (
                SELECT 1 FROM ticket_events
                WHERE ticket_id = @id
                  AND NOT (event_type = ANY(@excluded))
                  AND (created_utc, id) < (@oldestUtc, @oldestId))
            """;

        var excluded = excludedTypes.ToArray();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var newestFirst = (await conn.QueryAsync<TicketEvent>(new CommandDefinition(sql, new
        {
            id = ticketId,
            excluded,
            before = beforeEventId,
            until = untilEventId,
            limit,
        }, cancellationToken: ct))).ToList();
        if (newestFirst.Count == 0) return new TicketTimelineSlice(Array.Empty<TicketEvent>(), false);

        var oldest = newestFirst[^1];
        var hasMore = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(moreSql, new
        {
            id = ticketId,
            excluded,
            oldestUtc = oldest.CreatedUtc,
            oldestId = oldest.Id,
        }, cancellationToken: ct));
        newestFirst.Reverse();
        return new TicketTimelineSlice(newestFirst, hasMore);
    }

    // Same rule as the detail page used client-side: a GUID-shaped
    // data-order-id only (a hand-crafted attribute in an inbound mail can't
    // smuggle an arbitrary string into /api/orders/{id}); first seen wins.
    private static readonly Regex PillTagRegex = new(
        "<[^>]*\\bdata-order-id\\s*=\\s*\"([^\"]*)\"[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LabelAttrRegex = new(
        "\\bdata-label\\s*=\\s*\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GuidRegex = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.Compiled);

    internal static IReadOnlyList<TaggedOrderRef> ExtractTaggedOrders(IEnumerable<string?> bodies)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var html in bodies)
        {
            if (string.IsNullOrEmpty(html)) continue;
            foreach (Match tag in PillTagRegex.Matches(html))
            {
                var id = tag.Groups[1].Value.Trim().ToLowerInvariant();
                if (!GuidRegex.IsMatch(id) || seen.ContainsKey(id)) continue;
                var label = LabelAttrRegex.Match(tag.Value) is { Success: true } m
                    ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)
                    : string.Empty;
                seen[id] = label;
                order.Add(id);
            }
        }
        return order.Select(id => new TaggedOrderRef(id, seen[id])).ToList();
    }

    private sealed class TypeCountRow
    {
        public string EventType { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
