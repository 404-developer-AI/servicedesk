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
    public async Task<SplitResult?> SplitAsync(
        Guid sourceTicketId,
        long sourceMailEventId,
        string newSubject,
        Guid actorUserId,
        string? overrideBodyHtml,
        string? overrideBodyText,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the source row first so a concurrent merge/delete can't race us.
        const string lockSql = """
            SELECT id AS Id, number AS Number, queue_id AS QueueId,
                   requester_contact_id AS RequesterContactId,
                   company_id AS CompanyId,
                   awaiting_company_assignment AS AwaitingCompanyAssignment,
                   company_resolved_via AS CompanyResolvedVia,
                   merged_into_ticket_id AS MergedIntoTicketId,
                   ticket_type_id AS TicketTypeId
            FROM tickets WHERE id = @sourceTicketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var source = await conn.QueryFirstOrDefaultAsync<SplitSourceRow>(
            new CommandDefinition(lockSql, new { sourceTicketId }, tx, cancellationToken: ct));
        if (source is null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, 0, SplitFailureReason.SourceNotFound);
        }
        if (source.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.SourceMerged);
        }

        // Verify the mail event belongs to this ticket and is the right type.
        // We pull the body and the linked mail row in one shot so we can stamp
        // a system-note that includes the original mail subject.
        const string mailEventSql = """
            SELECT e.id AS Id, e.event_type AS EventType,
                   e.body_text AS BodyText, e.body_html AS BodyHtml,
                   e.author_contact_id AS AuthorContactId,
                   e.created_utc AS CreatedUtc,
                   m.id AS MailMessageId, m.subject AS MailSubject
            FROM ticket_events e
            LEFT JOIN mail_messages m ON m.ticket_event_id = e.id
            WHERE e.id = @sourceMailEventId AND e.ticket_id = @sourceTicketId
            """;
        var mailEvent = await conn.QueryFirstOrDefaultAsync<SplitMailEventRow>(
            new CommandDefinition(mailEventSql, new { sourceMailEventId, sourceTicketId }, tx, cancellationToken: ct));
        if (mailEvent is null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.MailEventNotFound);
        }
        if (!string.Equals(mailEvent.EventType, "MailReceived", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.NotAMailEvent);
        }

        // Resolve system defaults for the new ticket. Priority + status carry
        // is_default; queue does not, so we fall back to the lowest-sort active
        // queue. All three must exist for the new ticket to be valid.
        var defaultStatusId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM statuses WHERE is_default = TRUE AND is_active = TRUE LIMIT 1",
            transaction: tx, cancellationToken: ct));
        var defaultPriorityId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM priorities WHERE is_default = TRUE AND is_active = TRUE LIMIT 1",
            transaction: tx, cancellationToken: ct));
        var defaultQueueId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM queues WHERE is_active = TRUE ORDER BY sort_order, name LIMIT 1",
            transaction: tx, cancellationToken: ct));
        if (defaultStatusId is null || defaultPriorityId is null || defaultQueueId is null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.DefaultsMissing);
        }

        // Create the new ticket. Requester + company + ticket type carry over
        // from the source (the email came from the same person on the same kind
        // of ticket), but queue/priority/status are system defaults so the agent
        // can re-triage explicitly. Source field 'Split' marks the intake
        // channel for reporting.
        const string insertTicketSql = """
            INSERT INTO tickets (subject, requester_contact_id, queue_id,
                                 status_id, priority_id, source, ticket_type_id,
                                 company_id, awaiting_company_assignment, company_resolved_via,
                                 split_from_ticket_id, split_from_utc, split_from_user_id)
            VALUES (@Subject, @RequesterContactId, @QueueId,
                    @StatusId, @PriorityId, 'Split', @TicketTypeId,
                    @CompanyId, @AwaitingCompanyAssignment, @CompanyResolvedVia,
                    @SplitFromTicketId, now(), @SplitFromUserId)
            RETURNING id, number
            """;
        var newRow = await conn.QueryFirstAsync<(Guid Id, long Number)>(
            new CommandDefinition(insertTicketSql, new
            {
                Subject = newSubject,
                source.RequesterContactId,
                QueueId = defaultQueueId.Value,
                StatusId = defaultStatusId.Value,
                PriorityId = defaultPriorityId.Value,
                source.TicketTypeId,
                source.CompanyId,
                source.AwaitingCompanyAssignment,
                source.CompanyResolvedVia,
                SplitFromTicketId = sourceTicketId,
                SplitFromUserId = actorUserId,
            }, tx, cancellationToken: ct));

        // Description = the mail body. The caller passes the cid-rewritten HTML
        // (mail-timeline-enricher output) so inline `cid:` references resolve
        // against the source mail's attachment URLs. Falls back to the raw
        // event body when the enricher didn't produce a rewrite (e.g. a mail
        // without inline attachments).
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
            VALUES (@ticketId, @bodyText, @bodyHtml)
            """, new
        {
            ticketId = newRow.Id,
            bodyText = overrideBodyText ?? mailEvent.BodyText ?? string.Empty,
            bodyHtml = overrideBodyHtml ?? mailEvent.BodyHtml,
        }, tx, cancellationToken: ct));

        // Created event mirrors what CreateAsync writes — same shape so list/
        // detail readers don't need to special-case split origins.
        var createdMeta = JsonSerializer.Serialize(new
        {
            source = "Split",
            splitFromTicketId = sourceTicketId.ToString(),
            splitFromTicketNumber = source.Number,
            splitFromMailEventId = sourceMailEventId,
            splitFromMailMessageId = mailEvent.MailMessageId?.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id,
                                       body_text, body_html, metadata, is_internal)
            VALUES (@ticketId, 'Created', @authorContactId, NULL, NULL, @metadata::jsonb, FALSE)
            """, new
        {
            ticketId = newRow.Id,
            authorContactId = source.RequesterContactId,
            metadata = createdMeta,
        }, tx, cancellationToken: ct));

        // System-note on the new ticket pointing back to the source.
        var newNoteMeta = JsonSerializer.Serialize(new
        {
            splitFromTicketId = sourceTicketId.ToString(),
            splitFromTicketNumber = source.Number,
            splitFromMailEventId = sourceMailEventId,
            actorUserId = actorUserId.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id,
                                       metadata, is_internal, body_text)
            VALUES (@ticketId, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            ticketId = newRow.Id,
            actorUserId,
            metadata = newNoteMeta,
            body = $"Split from #{source.Number}.",
        }, tx, cancellationToken: ct));

        // System-note on the source ticket pointing forward to the new one.
        var sourceNoteMeta = JsonSerializer.Serialize(new
        {
            splitIntoTicketId = newRow.Id.ToString(),
            splitIntoTicketNumber = newRow.Number,
            sourceMailEventId,
            actorUserId = actorUserId.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id,
                                       metadata, is_internal, body_text)
            VALUES (@sourceTicketId, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            sourceTicketId,
            actorUserId,
            metadata = sourceNoteMeta,
            body = $"Split into #{newRow.Number}.",
        }, tx, cancellationToken: ct));

        // Bump the source ticket's updated_utc so the list refreshes.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @sourceTicketId",
            new { sourceTicketId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new SplitResult(true, newRow.Id, newRow.Number, source.Number, null);
    }

    private sealed class SplitSourceRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public Guid QueueId { get; set; }
        public Guid RequesterContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public bool AwaitingCompanyAssignment { get; set; }
        public string? CompanyResolvedVia { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
        public Guid TicketTypeId { get; set; }
    }

    private sealed class SplitMailEventRow
    {
        public long Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string? BodyText { get; set; }
        public string? BodyHtml { get; set; }
        public Guid? AuthorContactId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Guid? MailMessageId { get; set; }
        public string? MailSubject { get; set; }
    }

}
