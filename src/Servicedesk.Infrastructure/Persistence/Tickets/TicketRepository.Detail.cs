using Servicedesk.Infrastructure.Access;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public sealed partial class TicketRepository
{
    public async Task<Ticket?> GetCoreAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Ticket>(new CommandDefinition(
            TicketCoreSql, new { id }, cancellationToken: ct));
    }

    public async Task<TicketEvent?> GetEventAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = """
            SELECT e.id AS Id, e.ticket_id AS TicketId, e.event_type AS EventType,
                   e.author_user_id AS AuthorUserId, e.author_contact_id AS AuthorContactId,
                   COALESCE(au.email, NULLIF(CONCAT_WS(' ', ac.first_name, ac.last_name), ''), e.metadata->>'authorName') AS AuthorName,
                   e.body_text AS BodyText, e.body_html AS BodyHtml,
                   e.metadata::text AS MetadataJson, e.is_internal AS IsInternal,
                   e.created_utc AS CreatedUtc,
                   e.edited_utc AS EditedUtc, e.edited_by_user_id AS EditedByUserId
            FROM ticket_events e
            LEFT JOIN users    au ON au.id = e.author_user_id
            LEFT JOIN contacts ac ON ac.id = e.author_contact_id
            WHERE e.id = @eventId AND e.ticket_id = @ticketId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketEvent>(new CommandDefinition(
            sql, new { ticketId, eventId }, cancellationToken: ct));
    }

    public async Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        const string ticketSql = TicketCoreSql;
        const string bodySql = """
            SELECT ticket_id AS TicketId, body_text AS BodyText, body_html AS BodyHtml
            FROM ticket_bodies WHERE ticket_id = @id
            """;
        const string eventsSql = """
            SELECT e.id AS Id, e.ticket_id AS TicketId, e.event_type AS EventType,
                   e.author_user_id AS AuthorUserId, e.author_contact_id AS AuthorContactId,
                   COALESCE(au.email, NULLIF(CONCAT_WS(' ', ac.first_name, ac.last_name), ''), e.metadata->>'authorName') AS AuthorName,
                   e.body_text AS BodyText, e.body_html AS BodyHtml,
                   e.metadata::text AS MetadataJson, e.is_internal AS IsInternal,
                   e.created_utc AS CreatedUtc,
                   e.edited_utc AS EditedUtc, e.edited_by_user_id AS EditedByUserId
            FROM ticket_events e
            LEFT JOIN users    au ON au.id = e.author_user_id
            LEFT JOIN contacts ac ON ac.id = e.author_contact_id
            WHERE e.ticket_id = @id ORDER BY e.created_utc, e.id
            """;
        return await LoadDetailAsync(ticketSql, bodySql, eventsSql, id, ct);
    }

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
                   is_research AS IsResearch,
                   description_internal AS DescriptionInternal
            FROM tickets WHERE id = @id AND is_deleted = FALSE
            """;

    private async Task<TicketDetail?> LoadDetailAsync(
        string ticketSql, string bodySql, string eventsSql, Guid id, CancellationToken ct)
    {
        const string pinsSql = """
            SELECT p.id AS Id, p.event_id AS EventId, p.ticket_id AS TicketId,
                   p.pinned_by_user_id AS PinnedByUserId,
                   u.email AS PinnedByName,
                   p.remark AS Remark,
                   p.created_utc AS CreatedUtc
            FROM ticket_event_pins p
            JOIN users u ON u.id = p.pinned_by_user_id
            WHERE p.ticket_id = @id
            ORDER BY p.created_utc
            """;

        // v0.0.101 — one round-trip: the four statements go out as a single
        // batch and the result sets are read back in order. Ticket open is
        // the hottest read path in the app; before this it was four
        // sequential awaits on the same connection.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            ticketSql + ";\n" + bodySql + ";\n" + eventsSql + ";\n" + pinsSql,
            new { id }, cancellationToken: ct));

        var ticket = await grid.ReadFirstOrDefaultAsync<Ticket>();
        var body = await grid.ReadFirstOrDefaultAsync<TicketBody>()
            ?? new TicketBody(id, string.Empty, null);
        var events = (await grid.ReadAsync<TicketEvent>()).ToList();
        var pins = (await grid.ReadAsync<TicketEventPin>()).ToList();
        if (ticket is null) return null;

        return new TicketDetail(ticket, body, events, pins);
    }

    public async Task<TicketDetailRelations?> GetDetailRelationsAsync(Guid ticketId, CancellationToken ct)
    {
        // Nine statements, one batch, one connection. Every branch is an
        // indexed point lookup or a sparse-index scan (ix_tickets_merged_into,
        // ix_tickets_split_from, ix_tickets_parent), so the cost is dominated
        // by the round-trip we are saving, not by the queries.
        const string sql = """
            -- 0: existence + merge/split actors and counterpart numbers
            SELECT mu.email  AS MergedByUserName,
                   mt.number AS MergedIntoNumber,
                   st.number AS SplitFromNumber,
                   su.email  AS SplitFromUserName
            FROM tickets t
            LEFT JOIN users   mu ON mu.id = t.merged_by_user_id
            LEFT JOIN tickets mt ON mt.id = t.merged_into_ticket_id
            LEFT JOIN tickets st ON st.id = t.split_from_ticket_id
            LEFT JOIN users   su ON su.id = t.split_from_user_id
            WHERE t.id = @ticketId AND t.is_deleted = FALSE;

            -- 1: "Merged from #A, #B" (chronological)
            SELECT number FROM tickets
            WHERE merged_into_ticket_id = @ticketId
            ORDER BY merged_utc NULLS LAST, number;

            -- 2: "Split into #A, #B" (chronological)
            SELECT id AS Id, number AS Number FROM tickets
            WHERE split_from_ticket_id = @ticketId AND is_deleted = FALSE
            ORDER BY split_from_utc NULLS LAST, number;

            -- 3: linked parent summary (null when no parent)
            SELECT p.id                AS ParentTicketId,
                   p.number            AS ParentNumber,
                   u.email             AS LinkedByName,
                   t.parent_linked_utc AS LinkedUtc
            FROM tickets t
            JOIN tickets p ON p.id = t.parent_ticket_id
            LEFT JOIN users u ON u.id = t.parent_linked_by_user_id
            WHERE t.id = @ticketId AND t.parent_ticket_id IS NOT NULL;

            -- 4: linked child tickets
            SELECT id AS Id, number AS Number FROM tickets
            WHERE parent_ticket_id = @ticketId AND is_deleted = FALSE
            ORDER BY number;

            -- 5: company-alert source: the ticket's own frozen company first,
            --    else the requester's *active* primary company (v0.0.66 rule).
            SELECT c.id                AS CompanyId,
                   c.name              AS CompanyName,
                   c.code              AS Code,
                   c.alert_text        AS AlertText,
                   c.alert_on_create   AS AlertOnCreate,
                   c.alert_on_open     AS AlertOnOpen,
                   c.alert_on_open_mode AS AlertOnOpenMode
            FROM tickets t
            JOIN companies c ON c.id = COALESCE(
                t.company_id,
                (SELECT cc.company_id
                   FROM contact_companies cc
                   JOIN companies pc ON pc.id = cc.company_id AND pc.is_active = TRUE
                  WHERE cc.contact_id = t.requester_contact_id AND cc.role = 'primary'
                  LIMIT 1))
            WHERE t.id = @ticketId;

            -- 6: linked project summary (null when not linked) — v0.0.105
            SELECT p.id     AS ProjectTicketId,
                   p.number AS ProjectNumber,
                   p.subject AS ProjectSubject,
                   u.email  AS LinkedByName
            FROM tickets t
            JOIN tickets p ON p.id = t.project_ticket_id
            LEFT JOIN users u ON u.id = t.project_linked_by_user_id
            WHERE t.id = @ticketId AND t.project_ticket_id IS NOT NULL;

            -- 7: how many tickets are linked to this ticket as their project
            SELECT COUNT(*) FROM tickets
            WHERE project_ticket_id = @ticketId AND is_deleted = FALSE;

            -- 8: how many companies the requester is linked to (v0.1.33) —
            --    the side panel's "Contact not linked" banner needs it on the
            --    first render instead of after its own lookup (layout shift).
            SELECT COUNT(*) FROM contact_companies cc
            JOIN tickets t ON t.requester_contact_id = cc.contact_id
            WHERE t.id = @ticketId;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));

        var head = await grid.ReadFirstOrDefaultAsync<RelationsHeadRow>();
        var mergedSources = (await grid.ReadAsync<long>()).ToList();
        var splitChildren = (await grid.ReadAsync<SplitChildTicket>()).ToList();
        var parent = await grid.ReadFirstOrDefaultAsync<ParentTicketSummary>();
        var children = (await grid.ReadAsync<LinkedChildTicket>()).ToList();
        var companyAlert = await grid.ReadFirstOrDefaultAsync<TicketCompanyAlertSource>();
        var project = await grid.ReadFirstOrDefaultAsync<ProjectTicketSummary>();
        var projectLinkedCount = await grid.ReadFirstAsync<int>();
        var requesterCompanyLinkCount = await grid.ReadFirstAsync<int>();
        if (head is null) return null;

        return new TicketDetailRelations(
            MergedSourceTicketNumbers: mergedSources,
            MergedByUserName: head.MergedByUserName,
            MergedIntoTicketNumber: head.MergedIntoNumber?.ToString(),
            SplitFromTicketNumber: head.SplitFromNumber?.ToString(),
            SplitFromUserName: head.SplitFromUserName,
            SplitChildren: splitChildren,
            Parent: parent,
            ChildTickets: children,
            CompanyAlert: companyAlert,
            Project: project,
            ProjectLinkedTicketCount: projectLinkedCount,
            RequesterCompanyLinkCount: requesterCompanyLinkCount);
    }

    private sealed class RelationsHeadRow
    {
        public string? MergedByUserName { get; set; }
        public long? MergedIntoNumber { get; set; }
        public long? SplitFromNumber { get; set; }
        public string? SplitFromUserName { get; set; }
    }


    public async Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct)
    {
        // Uses ix_tickets_queue_status_updated as a count-only index scan.
        // At 1M rows with the partial index this is <20ms — no separate
        // counters table needed yet.
        const string sql = """
            SELECT t.queue_id AS QueueId, count(*)::int AS OpenCount
            FROM tickets t
            JOIN statuses s ON s.id = t.status_id
            WHERE t.is_deleted = FALSE AND s.state_category NOT IN ('Resolved','Closed')
            GROUP BY t.queue_id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid QueueId, int OpenCount)>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToDictionary(r => r.QueueId, r => r.OpenCount);
    }

    public Task<Guid?> GetMergedIntoAsync(Guid ticketId, CancellationToken ct)
    {
        // Tiny lookup used by the mail-ingest resolver to follow a redirect
        // chain hop-by-hop. Soft-deleted tickets still expose the pointer so a
        // mid-flight delete doesn't strand the redirect.
        const string sql = "SELECT merged_into_ticket_id FROM tickets WHERE id = @ticketId";
        return GetMergedIntoCoreAsync(sql, ticketId, ct);
    }

    private async Task<Guid?> GetMergedIntoCoreAsync(string sql, Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
    }

}
