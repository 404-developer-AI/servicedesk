using System.Data.Common;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// Who a field write is made by. An agent write stamps the agent as the
/// author of every change event; a trigger write has no author and stamps
/// <c>triggered_by</c> (plus <c>on_behalf_of</c> when the pass runs inside an
/// agent's request, see <see cref="TriggerOrigin"/>) so the timeline shows
/// the trigger instead of an avatar.
public abstract record TicketWriteOrigin
{
    private TicketWriteOrigin() { }

    public sealed record Agent(Guid UserId) : TicketWriteOrigin;
    public sealed record Trigger(Guid TriggerId) : TicketWriteOrigin;
}

public enum TicketFieldWriteStatus
{
    Applied,
    /// Nothing differed from the locked row; no write, no event.
    NoOp,
    TicketGone,
    /// A target id (queue, status, priority, category, user) does not exist.
    TargetNotFound,
    /// The effective target status (after a queue auto-flip) is blocked by
    /// a checklist with required open items — judged under the row lock,
    /// so it also catches an item reopened after the caller's precheck.
    ChecklistIncomplete,
}

/// One column change the write made, as recorded in its timeline event.
public sealed record TicketFieldChange(
    string EventType, string Column, Guid? From, Guid? To, string? FromName, string? ToName);

public sealed record TicketFieldWriteResult(
    TicketFieldWriteStatus Status,
    IReadOnlyList<TicketFieldChange> Changes,
    string? MissingTarget = null,
    IReadOnlyList<Checklists.ChecklistBlocker>? Blockers = null,
    Guid? BlockedStatusId = null)
{
    public TicketFieldChange? Change(string column) => Changes.FirstOrDefault(c => c.Column == column);
}

/// The agent write path's form of <see cref="TicketFieldWriteStatus.TargetNotFound"/>:
/// the update names a queue, status, priority, category or user that does
/// not exist (a stale form, a hand-rolled call, or a row deleted since).
/// PATCH answers 400 and a bulk leg is skipped — never a 500.
public sealed class TicketFieldTargetNotFoundException(string target)
    : InvalidOperationException($"Ticket update refers to an unknown {target}.")
{
    public const string Code = "unknown_target";

    /// "queue", "status", "priority", "category" or "user".
    public string Target { get; } = target;
}

/// The one write path for ticket field changes (v0.1.65 — ROADMAP §2 C3).
/// Agent PATCHes, bulk legs and trigger field actions all land here, so the
/// side effects of a change cannot drift between them: the queue → status
/// auto-flip, the Resolved/Closed stamps, the Pending-till auto-clear, the
/// change events and their names. The business rules that decide whether a
/// change is allowed at all live in <see cref="TicketFieldRules"/>; this
/// class only applies an update that already passed them.
///
/// Reads the row <c>FOR UPDATE</c> and compares against it, so a no-op is
/// judged on the current row, not on a caller's (possibly stale) snapshot.
///
/// One rule is re-checked here, under that lock: the checklist close block
/// for the effective target status. The rules' precheck runs before the
/// lock and does not see a queue auto-flip. Every checklist write touches
/// the ticket row in its own transaction (counter update; event rows whose
/// foreign key takes FOR KEY SHARE), which conflicts with this lock, so a
/// checklist change either committed before this check or waits until the
/// status change is done — never in between. Keep the read FOR UPDATE: a
/// FOR NO KEY UPDATE would not conflict with the foreign-key lock.
public sealed class TicketFieldWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Checklists.IChecklistCloseGuard _checklistGuard;

    public TicketFieldWriter(NpgsqlDataSource dataSource, Checklists.IChecklistCloseGuard checklistGuard)
    {
        _dataSource = dataSource;
        _checklistGuard = checklistGuard;
    }

    public async Task<TicketFieldWriteResult> ApplyAsync(
        Guid ticketId, TicketFieldUpdate update, TicketWriteOrigin origin, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string readSql = """
            SELECT queue_id AS QueueId, status_id AS StatusId,
                   (SELECT s.state_category FROM statuses s WHERE s.id = tickets.status_id) AS StatusCategory,
                   priority_id AS PriorityId,
                   category_id AS CategoryId, assignee_user_id AS AssigneeUserId,
                   pending_till_utc AS PendingTillUtc,
                   is_callback AS IsCallback, is_research AS IsResearch,
                   description_internal AS DescriptionInternal
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketFieldSnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) return await RollbackAsync(tx, TicketFieldWriteStatus.TicketGone, ct);

        // v0.0.40 — auto-flip status when a queue change would land the
        // ticket on a status outside the new queue's allowed-list. The UI
        // filters prevent the common case; this is the runtime safety net
        // for API calls and trigger moves alike. We rebind `update` so every
        // downstream branch (events, state-category side effects, SQL
        // parameters) picks up the flip without further wiring.
        if (update.QueueId.HasValue && update.QueueId != current.QueueId)
        {
            var scope = await conn.QueryFirstOrDefaultAsync<QueueStatusScopeRow>(
                new CommandDefinition(
                    "SELECT allowed_status_ids AS AllowedStatusIds, default_status_id AS DefaultStatusId FROM queues WHERE id = @id",
                    new { id = update.QueueId.Value }, tx, cancellationToken: ct));
            if (scope is not null
                && scope.AllowedStatusIds is { Length: > 0 }
                && scope.DefaultStatusId.HasValue)
            {
                var effective = update.StatusId ?? current.StatusId;
                if (!scope.AllowedStatusIds.Contains(effective))
                {
                    update = update with { StatusId = scope.DefaultStatusId };
                }
            }
        }

        // The close block, judged on the locked row for the status the write
        // will actually set (explicit or auto-flipped).
        if (update.StatusId is Guid target && target != current.StatusId)
        {
            var blockers = await _checklistGuard.FindBlockersAsync(ticketId, target, ct);
            if (blockers.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return new TicketFieldWriteResult(TicketFieldWriteStatus.ChecklistIncomplete,
                    Array.Empty<TicketFieldChange>(), Blockers: blockers, BlockedStatusId: target);
            }
        }

        var sets = new List<string>();
        var changes = new List<TicketFieldChange>();
        var flagEvents = new List<(string EventType, string MetadataJson)>();

        // Human-readable names so change events store "New → In Progress"
        // rather than raw UUIDs. Table names are compile-time constants.
        async Task<string?> LookupNameAsync(string table, Guid? id)
        {
            if (!id.HasValue) return null;
            var col = table == "users" ? "email" : "name";
            return await conn.ExecuteScalarAsync<string>(
                new CommandDefinition($"SELECT {col} FROM {table} WHERE id = @id",
                    new { id = id.Value }, tx, cancellationToken: ct));
        }

        // A change to a target that must exist. Returns false (and the
        // caller rolls back) when the target id is unknown.
        async Task<bool> AddChangeAsync(string eventType, string column, string table, Guid? from, Guid? to, string setSql)
        {
            var toName = await LookupNameAsync(table, to);
            if (to.HasValue && toName is null) return false;
            sets.Add(setSql);
            changes.Add(new TicketFieldChange(eventType, column, from, to, await LookupNameAsync(table, from), toName));
            return true;
        }

        if (update.QueueId.HasValue && update.QueueId != current.QueueId
            && !await AddChangeAsync("QueueChange", "queue_id", "queues", current.QueueId, update.QueueId, "queue_id = @NewQueueId"))
            return await RollbackAsync(tx, TicketFieldWriteStatus.TargetNotFound, ct, "queue");

        string? toCategory = null;
        if (update.StatusId.HasValue && update.StatusId != current.StatusId)
        {
            if (!await AddChangeAsync("StatusChange", "status_id", "statuses", current.StatusId, update.StatusId, "status_id = @NewStatusId"))
                return await RollbackAsync(tx, TicketFieldWriteStatus.TargetNotFound, ct, "status");
            toCategory = await StateCategoryAsync(conn, tx, update.StatusId.Value, ct);
        }

        if (update.PriorityId.HasValue && update.PriorityId != current.PriorityId
            && !await AddChangeAsync("PriorityChange", "priority_id", "priorities", current.PriorityId, update.PriorityId, "priority_id = @NewPriorityId"))
            return await RollbackAsync(tx, TicketFieldWriteStatus.TargetNotFound, ct, "priority");

        if (update.ClearCategory && current.CategoryId.HasValue)
            await AddChangeAsync("CategoryChange", "category_id", "categories", current.CategoryId, null, "category_id = NULL");
        else if (!update.ClearCategory && update.CategoryId.HasValue && update.CategoryId != current.CategoryId
                 && !await AddChangeAsync("CategoryChange", "category_id", "categories", current.CategoryId, update.CategoryId, "category_id = @NewCategoryId"))
            return await RollbackAsync(tx, TicketFieldWriteStatus.TargetNotFound, ct, "category");

        // v0.0.78 — ClearAssignee is the only way to unassign (a null
        // AssigneeUserId means "not provided"); a redundant clear is a no-op.
        if (update.ClearAssignee && current.AssigneeUserId.HasValue)
            await AddChangeAsync("AssignmentChange", "assignee_user_id", "users", current.AssigneeUserId, null, "assignee_user_id = NULL");
        else if (!update.ClearAssignee && update.AssigneeUserId.HasValue && update.AssigneeUserId != current.AssigneeUserId
                 && !await AddChangeAsync("AssignmentChange", "assignee_user_id", "users", current.AssigneeUserId, update.AssigneeUserId, "assignee_user_id = @NewAssigneeUserId"))
            return await RollbackAsync(tx, TicketFieldWriteStatus.TargetNotFound, ct, "user");

        // v0.1.17 — Call-back / Research flags; v0.1.49 — description
        // visibility. Same-value writes are a no-op (no event).
        if (update.IsCallback.HasValue && update.IsCallback.Value != current.IsCallback)
        {
            sets.Add("is_callback = @NewIsCallback");
            flagEvents.Add(("TicketFlagChange", JsonSerializer.Serialize(
                new { flag = "callback", from = current.IsCallback, to = update.IsCallback.Value })));
        }
        if (update.IsResearch.HasValue && update.IsResearch.Value != current.IsResearch)
        {
            sets.Add("is_research = @NewIsResearch");
            flagEvents.Add(("TicketFlagChange", JsonSerializer.Serialize(
                new { flag = "research", from = current.IsResearch, to = update.IsResearch.Value })));
        }
        if (update.DescriptionInternal.HasValue && update.DescriptionInternal.Value != current.DescriptionInternal)
        {
            sets.Add("description_internal = @NewDescriptionInternal");
            flagEvents.Add(("TicketFlagChange", JsonSerializer.Serialize(
                new { flag = "descriptionInternal", from = current.DescriptionInternal, to = update.DescriptionInternal.Value })));
        }

        if (update.Subject is not null) sets.Add("subject = @NewSubject");
        var bodyChanged = update.BodyText is not null || update.BodyHtml is not null;

        // v0.0.37 — gate pending-till writes against the *effective* status
        // (after a same-update flip): only a ticket that is, or ends up, in a
        // Pending status gets the value. A value sent alongside a move out of
        // Pending is ignored (the auto-clear below wipes the column), and so
        // is one on a ticket that stays non-Pending (the rules refuse that
        // first; this keeps the invariant inside the row lock). Runs before
        // the no-op short-circuit so a pending-till-only update works.
        var writePendingTill =
            update.PendingTillUtc.HasValue
            && (toCategory ?? current.StatusCategory) == "Pending";
        if (writePendingTill) sets.Add("pending_till_utc = @NewPendingTillUtc");
        // v0.1.44 — leave a trace: who moved the reminder, from when to when.
        if (writePendingTill && !PendingTillRules.SameInstant(current.PendingTillUtc, update.PendingTillUtc))
        {
            flagEvents.Add(("PendingTillChange", JsonSerializer.Serialize(new
            {
                from = PendingTillRules.ToUtc(current.PendingTillUtc),
                to = PendingTillRules.ToUtc(update.PendingTillUtc),
            })));
        }

        if (sets.Count == 0 && !bodyChanged) return await RollbackAsync(tx, TicketFieldWriteStatus.NoOp, ct);

        sets.Add("updated_utc = now()");
        if (toCategory is not null)
        {
            if (toCategory == "Resolved") sets.Add("resolved_utc = COALESCE(resolved_utc, now())");
            if (toCategory == "Closed") sets.Add("closed_utc = COALESCE(closed_utc, now())");
            // Any non-Pending target wipes the reminder and its chained
            // trigger pointer. Invariant: pending_till_utc is only ever
            // non-null on a Pending-category ticket (v0.0.100 — a leaked
            // value made the scheduler re-evaluate a ticket every tick).
            if (toCategory != "Pending")
            {
                sets.Add("pending_till_utc = NULL");
                sets.Add("pending_till_next_trigger_id = NULL");
            }
        }

        if (sets.Count > 1) // more than just updated_utc
        {
            // Every fragment in `sets` is a compile-time constant above.
            await conn.ExecuteAsync(new CommandDefinition($"UPDATE tickets SET {string.Join(", ", sets)} WHERE id = @ticketId", new
            {
                ticketId,
                NewQueueId = update.QueueId,
                NewStatusId = update.StatusId,
                NewPriorityId = update.PriorityId,
                NewCategoryId = update.CategoryId,
                NewAssigneeUserId = update.AssigneeUserId,
                NewSubject = update.Subject,
                NewPendingTillUtc = update.PendingTillUtc,
                NewIsCallback = update.IsCallback,
                NewIsResearch = update.IsResearch,
                NewDescriptionInternal = update.DescriptionInternal,
            }, tx, cancellationToken: ct));
        }

        if (bodyChanged)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
                VALUES (@ticketId, COALESCE(@bodyText, ''), @bodyHtml)
                ON CONFLICT (ticket_id) DO UPDATE
                    SET body_text = COALESCE(@bodyText, ticket_bodies.body_text),
                        body_html = COALESCE(@bodyHtml, ticket_bodies.body_html)
                """,
                new { ticketId, bodyText = update.BodyText, bodyHtml = update.BodyHtml },
                tx, cancellationToken: ct));
            if (sets.Count <= 1)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tickets SET updated_utc = now() WHERE id = @ticketId",
                    new { ticketId }, tx, cancellationToken: ct));
            }
        }

        var events = changes.Select(c => (c.EventType, ChangeMetadata(c, current, toCategory))).Concat(flagEvents);
        await InsertEventsAsync(conn, tx, ticketId, events, update.BulkBatchId, origin, ct);

        await tx.CommitAsync(ct);
        return new TicketFieldWriteResult(TicketFieldWriteStatus.Applied, changes);
    }

    private static string ChangeMetadata(TicketFieldChange c, TicketFieldSnapshot current, string? toCategory) =>
        c.EventType == "StatusChange"
            ? JsonSerializer.Serialize(new
            {
                from = c.From, to = c.To, fromName = c.FromName, toName = c.ToName,
                fromCategory = current.StatusCategory, toCategory,
            })
            : JsonSerializer.Serialize(new { from = c.From, to = c.To, fromName = c.FromName, toName = c.ToName });

    private static async Task InsertEventsAsync(
        NpgsqlConnection conn, DbTransaction tx, Guid ticketId,
        IEnumerable<(string EventType, string MetadataJson)> events, Guid? bulkBatchId,
        TicketWriteOrigin origin, CancellationToken ct)
    {
        var authorUserId = origin is TicketWriteOrigin.Agent a ? a.UserId : (Guid?)null;
        // Trigger writes carry triggered_by (+ on_behalf_of) on every event.
        var originJson = origin is TicketWriteOrigin.Trigger t
            ? JsonSerializer.Serialize(TriggerOrigin.OnBehalfOfUserId is { } agent
                ? new Dictionary<string, object?> { ["triggered_by"] = t.TriggerId, ["on_behalf_of"] = agent }
                : new Dictionary<string, object?> { ["triggered_by"] = t.TriggerId })
            : null;
        var batch = bulkBatchId?.ToString();
        foreach (var (eventType, metadata) in events)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
                VALUES (@ticketId, @eventType, @authorUserId,
                        @metadata::jsonb
                          || COALESCE(@originJson::jsonb, '{}'::jsonb)
                          || CASE WHEN @batch IS NULL THEN '{}'::jsonb
                                  ELSE jsonb_build_object('bulk_batch_id', @batch::text) END,
                        @isInternal)
                """,
                // v0.1.17 — flag and pending-till changes are internal-only
                // (defence in depth on top of the portal's event allow-list).
                new
                {
                    ticketId, eventType, authorUserId, metadata, originJson, batch,
                    isInternal = eventType is "TicketFlagChange" or "PendingTillChange",
                },
                tx, cancellationToken: ct));
        }
    }

    private static async Task<string?> StateCategoryAsync(
        NpgsqlConnection conn, DbTransaction tx, Guid statusId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state_category FROM statuses WHERE id = @id",
            new { id = statusId }, tx, cancellationToken: ct));

    private static async Task<TicketFieldWriteResult> RollbackAsync(
        DbTransaction tx, TicketFieldWriteStatus status, CancellationToken ct, string? missingTarget = null)
    {
        await tx.RollbackAsync(ct);
        return new TicketFieldWriteResult(status, Array.Empty<TicketFieldChange>(), missingTarget);
    }

    private sealed record TicketFieldSnapshot(
        Guid QueueId, Guid StatusId, string? StatusCategory, Guid PriorityId, Guid? CategoryId, Guid? AssigneeUserId,
        DateTime? PendingTillUtc, bool IsCallback, bool IsResearch, bool DescriptionInternal);

    // Sealed class (not record) per the project's dapper_record_struct_null_bug
    // convention; default_status_id is nullable and may legitimately be null.
    private sealed class QueueStatusScopeRow
    {
        public Guid[] AllowedStatusIds { get; set; } = Array.Empty<Guid>();
        public Guid? DefaultStatusId { get; set; }
    }
}
