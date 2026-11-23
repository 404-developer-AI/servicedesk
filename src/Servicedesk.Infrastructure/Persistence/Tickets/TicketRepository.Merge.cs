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
    public async Task<MergeResult?> MergeAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        Guid actorUserId,
        bool acknowledgedCrossCustomer,
        CancellationToken ct)
    {
        if (sourceTicketId == targetTicketId)
            return new MergeResult(false, 0, 0, 0, false, MergeFailureReason.SameTicket);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock both rows up-front (deterministic order to avoid deadlocks with
        // a parallel A↔B merge). is_deleted=FALSE filter keeps soft-deleted
        // tickets out of the merge entirely.
        var orderedIds = sourceTicketId.CompareTo(targetTicketId) < 0
            ? new[] { sourceTicketId, targetTicketId }
            : new[] { targetTicketId, sourceTicketId };
        const string lockSql = """
            SELECT id AS Id, number AS Number, requester_contact_id AS RequesterContactId,
                   company_id AS CompanyId, merged_into_ticket_id AS MergedIntoTicketId,
                   description_internal AS DescriptionInternal
            FROM tickets WHERE id = @id AND is_deleted = FALSE
            FOR UPDATE
            """;
        var locked = new Dictionary<Guid, MergeLockedRow>();
        foreach (var id in orderedIds)
        {
            var row = await conn.QueryFirstOrDefaultAsync<MergeLockedRow>(
                new CommandDefinition(lockSql, new { id }, tx, cancellationToken: ct));
            if (row is not null) locked[id] = row;
        }

        if (!locked.TryGetValue(sourceTicketId, out var source))
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, 0, 0, false, MergeFailureReason.SourceNotFound);
        }
        if (!locked.TryGetValue(targetTicketId, out var target))
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, 0, false, MergeFailureReason.TargetNotFound);
        }
        if (source.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, target.Number, false, MergeFailureReason.AlreadyMerged);
        }
        if (target.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, target.Number, false, MergeFailureReason.AlreadyMerged);
        }

        // Cycle check: walk the target's existing merge chain (target itself is
        // not merged, so this is normally a no-op; defensive against future
        // multi-hop scenarios).
        var hop = target.MergedIntoTicketId;
        var hops = 0;
        while (hop is not null && hops++ < 16)
        {
            if (hop == sourceTicketId)
            {
                await tx.RollbackAsync(ct);
                return new MergeResult(false, 0, source.Number, target.Number, false, MergeFailureReason.WouldCycle);
            }
            hop = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT merged_into_ticket_id FROM tickets WHERE id = @id",
                new { id = hop }, tx, cancellationToken: ct));
        }

        var crossCustomer = source.RequesterContactId != target.RequesterContactId
            || source.CompanyId != target.CompanyId;
        if (crossCustomer && !acknowledgedCrossCustomer)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, target.Number, true, MergeFailureReason.CrossCustomerNotAcknowledged);
        }

        // Merged status row is seeded by TaxonomySeeder with slug='merged'.
        // We resolve by slug rather than caching the id so admins can re-color
        // or rename without a config change.
        var mergedStatusId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM statuses WHERE slug = 'merged' LIMIT 1",
            transaction: tx, cancellationToken: ct));
        if (mergedStatusId is null)
        {
            // Seeder hasn't run or row was force-deleted. Insert it inline so
            // the merge can complete; idempotent with the seeder's ON CONFLICT.
            mergedStatusId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO statuses (name, slug, state_category, color, icon, sort_order, is_active, is_system, is_default)
                VALUES ('Merged', 'merged', 'Closed', '#a855f7', 'git-merge', 60, TRUE, TRUE, FALSE)
                ON CONFLICT (slug) DO UPDATE SET updated_utc = now()
                RETURNING id
                """, transaction: tx, cancellationToken: ct));
        }

        // Re-point everything that hangs off the source ticket to the target.
        // ticket_events is the heaviest — we tag every moved event in metadata
        // so the timeline can render a "from #1234" badge.
        var moved = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_events
               SET ticket_id = @target,
                   metadata = jsonb_set(
                       jsonb_set(metadata, '{mergedFromTicketId}', to_jsonb(@source::text), TRUE),
                       '{mergedFromTicketNumber}', to_jsonb(@sourceNumber), TRUE)
             WHERE ticket_id = @source
            """, new { source = sourceTicketId, target = targetTicketId, sourceNumber = source.Number },
            tx, cancellationToken: ct));

        // The ticket_event_search trigger only fires on INSERT or on UPDATE OF
        // body_text/body_html — not on UPDATE OF ticket_id. We therefore
        // re-point the sidecar explicitly so search keeps returning hits for
        // moved events under the surviving ticket.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_event_search SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE mail_messages SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_event_pins SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_notifications SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE group_tags SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        // v0.1.54 — followers of the source keep following the surviving
        // ticket; someone who already follows the target keeps their row.
        // v0.1.58 — the personal note travels along; an existing target
        // row (and its note) wins.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_follows (user_id, ticket_id, followed_utc, last_seen_utc, note)
            SELECT user_id, @target, followed_utc, last_seen_utc, note FROM ticket_follows WHERE ticket_id = @source
            ON CONFLICT (user_id, ticket_id) DO NOTHING;
            DELETE FROM ticket_follows WHERE ticket_id = @source;
            """,
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE intake_form_instances SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        // Re-point logged time. Without this, agents who already booked
        // hours on the soon-to-be-merged source would either lose the
        // attribution entirely or be left chasing a tombstone ticket.
        // The count is included in the target's system-note metadata so
        // the audit story stays self-contained.
        var movedTimesheetEntries = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE timesheet_entries SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        // The original ticket-body of the source is a 1:1 row, not part of the
        // event stream. To preserve the requester's first message we project
        // it onto the target as a Comment event timestamped at the source's
        // creation. Then we replace the source body with a placeholder so the
        // merged ticket reads cleanly. The copy keeps the description's
        // visibility: an internal description becomes an internal comment,
        // never a customer-visible one (v0.1.49).
        var sourceBody = await conn.QueryFirstOrDefaultAsync<(string BodyText, string? BodyHtml)>(
            new CommandDefinition(
                "SELECT body_text AS BodyText, body_html AS BodyHtml FROM ticket_bodies WHERE ticket_id = @source",
                new { source = sourceTicketId }, tx, cancellationToken: ct));
        var sourceCreatedUtc = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT created_utc FROM tickets WHERE id = @source",
            new { source = sourceTicketId }, tx, cancellationToken: ct));
        var sourceRequesterId = source.RequesterContactId;
        if (!string.IsNullOrWhiteSpace(sourceBody.BodyText) || !string.IsNullOrWhiteSpace(sourceBody.BodyHtml))
        {
            var bodyMetadata = JsonSerializer.Serialize(new
            {
                mergedFromTicketId = sourceTicketId.ToString(),
                mergedFromTicketNumber = source.Number,
                isOriginalDescription = true,
            });
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ticket_events (ticket_id, event_type, author_contact_id,
                                           body_text, body_html, metadata, is_internal, created_utc)
                VALUES (@target, 'Comment', @authorContactId, @bodyText, @bodyHtml,
                        @metadata::jsonb, @isInternal, @createdUtc)
                """, new
            {
                target = targetTicketId,
                isInternal = source.DescriptionInternal,
                authorContactId = sourceRequesterId,
                bodyText = sourceBody.BodyText ?? string.Empty,
                bodyHtml = sourceBody.BodyHtml,
                metadata = bodyMetadata,
                createdUtc = sourceCreatedUtc,
            }, tx, cancellationToken: ct));
            moved += 1;
        }

        var targetNumber = target.Number;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_bodies
               SET body_text = @placeholder, body_html = NULL
             WHERE ticket_id = @source
            """, new
        {
            source = sourceTicketId,
            placeholder = $"This ticket was merged into #{targetNumber}.",
        }, tx, cancellationToken: ct));

        // Stamp the source ticket: status flip + merge pointer + closed_utc so
        // SLA-state and reporting queries treat it as a finalised terminal row.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tickets
               SET status_id            = @mergedStatusId,
                   merged_into_ticket_id = @target,
                   merged_utc            = now(),
                   merged_by_user_id     = @actorUserId,
                   resolved_utc          = COALESCE(resolved_utc, now()),
                   closed_utc            = COALESCE(closed_utc, now()),
                   -- v0.0.100: Merged is a Closed-category status, so the
                   -- reminder must go with it (same invariant as every
                   -- other status writer) — a merged source with a stale
                   -- pending_till_utc was an eternal scheduler candidate.
                   pending_till_utc      = NULL,
                   pending_till_next_trigger_id = NULL,
                   updated_utc           = now()
             WHERE id = @source
            """, new
        {
            source = sourceTicketId,
            target = targetTicketId,
            mergedStatusId,
            actorUserId,
        }, tx, cancellationToken: ct));

        // Bump the target's updated_utc so list views surface it.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @target",
            new { target = targetTicketId }, tx, cancellationToken: ct));

        // System-note timeline events on both tickets so the audit story is
        // self-contained even when the audit_log row isn't surfaced in the UI.
        var sourceNoteMeta = JsonSerializer.Serialize(new
        {
            mergedIntoTicketId = targetTicketId.ToString(),
            mergedIntoTicketNumber = targetNumber,
            actorUserId = actorUserId.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal, body_text)
            VALUES (@source, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            source = sourceTicketId,
            actorUserId,
            metadata = sourceNoteMeta,
            body = $"Merged into #{targetNumber}.",
        }, tx, cancellationToken: ct));

        var targetNoteMeta = JsonSerializer.Serialize(new
        {
            mergedFromTicketId = sourceTicketId.ToString(),
            mergedFromTicketNumber = source.Number,
            actorUserId = actorUserId.ToString(),
            movedEventCount = moved,
            movedTimesheetEntryCount = movedTimesheetEntries,
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal, body_text)
            VALUES (@target, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            target = targetTicketId,
            actorUserId,
            metadata = targetNoteMeta,
            body = $"Ticket #{source.Number} was merged into this ticket.",
        }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new MergeResult(true, moved, source.Number, targetNumber, crossCustomer, null);
    }

    private sealed class MergeLockedRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public Guid RequesterContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
        public bool DescriptionInternal { get; set; } = true;
    }

}
