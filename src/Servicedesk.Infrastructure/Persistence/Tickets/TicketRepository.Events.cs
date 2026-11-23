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
    public async Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition("SELECT EXISTS(SELECT 1 FROM tickets WHERE id = @ticketId AND is_deleted = FALSE)",
                new { ticketId }, tx, cancellationToken: ct));
        if (!exists) { await tx.RollbackAsync(ct); return null; }

        const string insertSql = """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, author_contact_id,
                                        body_text, body_html, is_internal, metadata)
            VALUES (@TicketId, @EventType, @AuthorUserId, @AuthorContactId,
                    @BodyText, @BodyHtml, @IsInternal, COALESCE(@MetadataJson::jsonb, '{}'::jsonb))
            RETURNING id AS Id, ticket_id AS TicketId, event_type AS EventType,
                      author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                      COALESCE(
                          (SELECT email FROM users WHERE id = author_user_id),
                          (SELECT CONCAT_WS(' ', first_name, last_name) FROM contacts WHERE id = author_contact_id)
                      ) AS AuthorName,
                      body_text AS BodyText, body_html AS BodyHtml,
                      metadata::text AS MetadataJson, is_internal AS IsInternal,
                      created_utc AS CreatedUtc,
                      edited_utc AS EditedUtc, edited_by_user_id AS EditedByUserId
            """;
        var evt = await conn.QuerySingleAsync<TicketEvent>(new CommandDefinition(insertSql, new
        {
            TicketId = ticketId,
            input.EventType,
            input.AuthorUserId,
            input.AuthorContactId,
            input.BodyText,
            input.BodyHtml,
            input.IsInternal,
            input.MetadataJson,
        }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @ticketId",
            new { ticketId }, tx, cancellationToken: ct));

        if (input.EventType == "Comment" && !input.IsInternal)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tickets SET first_response_utc = now() WHERE id = @ticketId AND first_response_utc IS NULL",
                new { ticketId }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return evt;
    }

    public async Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Fetch existing event and verify it belongs to this ticket + is editable
        const string selectSql = """
            SELECT event_type, body_text, body_html, is_internal
            FROM ticket_events
            WHERE id = @eventId AND ticket_id = @ticketId
            """;
        var current = await conn.QueryFirstOrDefaultAsync<(string EventType, string? BodyText, string? BodyHtml, bool IsInternal)>(
            new CommandDefinition(selectSql, new { eventId, ticketId }, tx, cancellationToken: ct));
        if (current.EventType is null) { await tx.RollbackAsync(ct); return null; }

        // Only Comment, Note, Call and Mail events can be edited
        if (current.EventType != "Comment" && current.EventType != "Note" && current.EventType != "Mail" && current.EventType != "Call")
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // Determine next revision number
        var maxRevision = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                "SELECT MAX(revision_number) FROM ticket_event_revisions WHERE event_id = @eventId",
                new { eventId }, tx, cancellationToken: ct)) ?? 0;

        // Store old values as a revision
        const string insertRevisionSql = """
            INSERT INTO ticket_event_revisions (event_id, revision_number, body_text_before, body_html_before, is_internal_before, edited_by_user_id)
            VALUES (@eventId, @revisionNumber, @bodyTextBefore, @bodyHtmlBefore, @isInternalBefore, @editorUserId)
            """;
        await conn.ExecuteAsync(new CommandDefinition(insertRevisionSql, new
        {
            eventId,
            revisionNumber = maxRevision + 1,
            bodyTextBefore = current.BodyText,
            bodyHtmlBefore = current.BodyHtml,
            isInternalBefore = current.IsInternal,
            editorUserId = input.EditorUserId,
        }, tx, cancellationToken: ct));

        // v0.0.51 — keep event_type in sync with is_internal for the
        // Note/Comment pair. The timeline + PDF + customer-portal
        // filters render labels and visibility from event_type (Note =
        // "Internal note", Comment = "Reply"), so toggling is_internal
        // without flipping the type leaves a hybrid row that *looks*
        // unchanged in the UI even though the DB write succeeded. Mail
        // events keep their own type — flipping a sent mail to Note
        // would lose its provenance and the OutboundMailService /
        // MailIngestService rely on event_type='Mail' downstream.
        // RepostAsPublicReplyHandler already enforces the same pairing
        // when it duplicates an internal note as a public reply (see
        // its comment), so this brings the agent-edit path in line.
        var resolvedIsInternal = input.IsInternal ?? current.IsInternal;
        var resolvedEventType = current.EventType is "Note" or "Comment"
            ? (resolvedIsInternal ? "Note" : "Comment")
            : current.EventType;

        const string updateSql = """
            UPDATE ticket_events
            SET body_text = @bodyText,
                body_html = @bodyHtml,
                is_internal = @isInternal,
                event_type = @eventType,
                edited_utc = now(),
                edited_by_user_id = @editorUserId
            WHERE id = @eventId AND ticket_id = @ticketId
            RETURNING id AS Id, ticket_id AS TicketId, event_type AS EventType,
                      author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                      COALESCE(
                          (SELECT email FROM users WHERE id = author_user_id),
                          (SELECT CONCAT_WS(' ', first_name, last_name) FROM contacts WHERE id = author_contact_id)
                      ) AS AuthorName,
                      body_text AS BodyText, body_html AS BodyHtml,
                      metadata::text AS MetadataJson, is_internal AS IsInternal,
                      created_utc AS CreatedUtc,
                      edited_utc AS EditedUtc, edited_by_user_id AS EditedByUserId
            """;
        var updated = await conn.QuerySingleAsync<TicketEvent>(new CommandDefinition(updateSql, new
        {
            bodyText = input.BodyText ?? current.BodyText,
            bodyHtml = input.BodyHtml ?? current.BodyHtml,
            isInternal = resolvedIsInternal,
            eventType = resolvedEventType,
            editorUserId = input.EditorUserId,
            eventId,
            ticketId,
        }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @ticketId",
            new { ticketId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id AS Id, r.event_id AS EventId, r.revision_number AS RevisionNumber,
                   r.body_text_before AS BodyTextBefore, r.body_html_before AS BodyHtmlBefore,
                   r.is_internal_before AS IsInternalBefore,
                   r.edited_by_user_id AS EditedByUserId,
                   u.email AS EditedByName,
                   r.edited_utc AS EditedUtc
            FROM ticket_event_revisions r
            JOIN users u ON u.id = r.edited_by_user_id
            WHERE r.event_id = @eventId
              AND EXISTS (SELECT 1 FROM ticket_events WHERE id = @eventId AND ticket_id = @ticketId)
            ORDER BY r.revision_number DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var revisions = await conn.QueryAsync<TicketEventRevision>(
            new CommandDefinition(sql, new { eventId, ticketId }, cancellationToken: ct));
        return revisions.ToList();
    }

    public async Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO ticket_event_pins (event_id, ticket_id, pinned_by_user_id, remark)
            SELECT @eventId, @ticketId, @userId, @remark
            WHERE EXISTS (
                SELECT 1 FROM ticket_events
                WHERE id = @eventId AND ticket_id = @ticketId
                  AND event_type IN ('Comment','Note','Mail','MailReceived','MailSent','IntakeFormSubmitted')
            )
            ON CONFLICT (event_id) DO NOTHING
            RETURNING id AS Id, event_id AS EventId, ticket_id AS TicketId,
                      pinned_by_user_id AS PinnedByUserId,
                      (SELECT email FROM users WHERE id = pinned_by_user_id) AS PinnedByName,
                      remark AS Remark, created_utc AS CreatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var pin = await conn.QueryFirstOrDefaultAsync<TicketEventPin>(
            new CommandDefinition(sql, new { eventId, ticketId, userId, remark = remark ?? "" }, cancellationToken: ct));

        // If ON CONFLICT hit (already pinned), return the existing pin
        if (pin is null)
        {
            const string existingSql = """
                SELECT p.id AS Id, p.event_id AS EventId, p.ticket_id AS TicketId,
                       p.pinned_by_user_id AS PinnedByUserId,
                       u.email AS PinnedByName,
                       p.remark AS Remark, p.created_utc AS CreatedUtc
                FROM ticket_event_pins p
                JOIN users u ON u.id = p.pinned_by_user_id
                WHERE p.event_id = @eventId AND p.ticket_id = @ticketId
                """;
            pin = await conn.QueryFirstOrDefaultAsync<TicketEventPin>(
                new CommandDefinition(existingSql, new { eventId, ticketId }, cancellationToken: ct));
        }

        return pin;
    }

    public async Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = "DELETE FROM ticket_event_pins WHERE event_id = @eventId AND ticket_id = @ticketId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { eventId, ticketId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct)
    {
        const string sql = """
            UPDATE ticket_event_pins SET remark = @remark
            WHERE event_id = @eventId AND ticket_id = @ticketId
            RETURNING id AS Id, event_id AS EventId, ticket_id AS TicketId,
                      pinned_by_user_id AS PinnedByUserId,
                      (SELECT email FROM users WHERE id = pinned_by_user_id) AS PinnedByName,
                      remark AS Remark, created_utc AS CreatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketEventPin>(
            new CommandDefinition(sql, new { eventId, ticketId, remark = remark ?? "" }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<(Guid TicketId, long EventId)>> EventsBelongToTicketsAsync(
        IReadOnlyCollection<(Guid TicketId, long EventId)> pairs, CancellationToken ct)
    {
        var ok = new HashSet<(Guid TicketId, long EventId)>();
        if (pairs.Count == 0) return ok;
        var eventIds = pairs.Select(p => p.EventId).Distinct().ToArray();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EventTicketRow>(new CommandDefinition(
            "SELECT id AS EventId, ticket_id AS TicketId FROM ticket_events WHERE id = ANY(@eventIds)",
            new { eventIds }, cancellationToken: ct));
        var owner = rows.ToDictionary(r => r.EventId, r => r.TicketId);
        foreach (var p in pairs)
            if (owner.TryGetValue(p.EventId, out var t) && t == p.TicketId) ok.Add(p);
        return ok;
    }

    private sealed class EventTicketRow
    {
        public long EventId { get; set; }
        public Guid TicketId { get; set; }
    }

    public async Task<bool> EventBelongsToTicketAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM ticket_events WHERE id = @eventId AND ticket_id = @ticketId)";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { ticketId, eventId }, cancellationToken: ct));
    }

}
