using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Shared helper for the simple "change one ticket-field by id" actions
/// (<c>set_queue</c>, <c>set_priority</c>, <c>set_owner</c>, ...). Each
/// handler supplies the column name + lookup-table + event-type and the
/// helper does the rest: name lookup, UPDATE, system-actor INSERT into
/// <c>ticket_events</c>, and the change-event metadata <c>{ from, to,
/// fromName, toName, triggered_by }</c> shape that the timeline UI already
/// understands. Status-change has special bookkeeping (resolved_utc /
/// closed_utc) so it has its own method.
internal sealed class SystemFieldMutator
{
    private readonly NpgsqlDataSource _dataSource;

    public SystemFieldMutator(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<FieldChangeOutcome> ChangeFieldAsync(
        Guid ticketId,
        string columnName,
        string lookupTable,
        string lookupColumn,
        string eventType,
        Guid? currentValue,
        Guid newValue,
        Guid triggerId,
        CancellationToken ct)
    {
        if (currentValue.HasValue && currentValue.Value == newValue)
            return FieldChangeOutcome.AlreadyAtTarget(columnName);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var fromName = currentValue.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                $"SELECT {lookupColumn} FROM {lookupTable} WHERE id = @id",
                new { id = currentValue.Value }, tx, cancellationToken: ct))
            : null;
        var toName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT {lookupColumn} FROM {lookupTable} WHERE id = @id",
            new { id = newValue }, tx, cancellationToken: ct));
        if (toName is null)
        {
            await tx.RollbackAsync(ct);
            return FieldChangeOutcome.TargetNotFound(columnName, newValue);
        }

        var updateSql = $"UPDATE tickets SET {columnName} = @newValue, updated_utc = now() WHERE id = @ticketId AND is_deleted = FALSE";
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { newValue, ticketId }, tx, cancellationToken: ct));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return FieldChangeOutcome.TicketGone(columnName);
        }

        var metadata = TriggerEventMetadata.FieldChange(
            currentValue, newValue, fromName, toName, triggerId);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, @eventType, NULL, @metadata::jsonb, FALSE)
            """,
            new { ticketId, eventType, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return FieldChangeOutcome.Applied(columnName, currentValue, newValue, fromName, toName);
    }

    public async Task<FieldChangeOutcome> ChangeStatusAsync(
        Guid ticketId,
        Guid currentStatusId,
        Guid newStatusId,
        Guid triggerId,
        CancellationToken ct)
    {
        if (currentStatusId == newStatusId)
            return FieldChangeOutcome.AlreadyAtTarget("status_id");

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var fromName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM statuses WHERE id = @id", new { id = currentStatusId }, tx, cancellationToken: ct));
        var fromCategory = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state_category FROM statuses WHERE id = @id", new { id = currentStatusId }, tx, cancellationToken: ct));
        var toName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM statuses WHERE id = @id", new { id = newStatusId }, tx, cancellationToken: ct));
        var toCategory = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state_category FROM statuses WHERE id = @id", new { id = newStatusId }, tx, cancellationToken: ct));
        if (toName is null)
        {
            await tx.RollbackAsync(ct);
            return FieldChangeOutcome.TargetNotFound("status_id", newStatusId);
        }

        var sets = new List<string>
        {
            "status_id = @newStatusId",
            "updated_utc = now()",
        };
        if (toCategory == "Resolved") sets.Add("resolved_utc = COALESCE(resolved_utc, now())");
        if (toCategory == "Closed") sets.Add("closed_utc = COALESCE(closed_utc, now())");

        var updateSql = $"UPDATE tickets SET {string.Join(", ", sets)} WHERE id = @ticketId AND is_deleted = FALSE";
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { newStatusId, ticketId }, tx, cancellationToken: ct));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return FieldChangeOutcome.TicketGone("status_id");
        }

        var metadata = TriggerEventMetadata.FieldChange(
            currentStatusId, newStatusId, fromName, toName, triggerId,
            extra: new Dictionary<string, object?>
            {
                ["fromCategory"] = fromCategory,
                ["toCategory"] = toCategory,
            });

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'StatusChange', NULL, @metadata::jsonb, FALSE)
            """,
            new { ticketId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return FieldChangeOutcome.Applied("status_id", currentStatusId, newStatusId, fromName, toName);
    }

    public async Task<bool> SetPendingTillAsync(
        Guid ticketId,
        DateTime? newPendingTillUtc,
        Guid triggerId,
        Guid? nextTriggerId,
        CancellationToken ct)
    {
        // Write the pending-till + the chained-trigger pointer in one
        // statement. When `newPendingTillUtc` is null (clear) the pointer
        // is also cleared — chaining only makes sense alongside a future
        // wake-up moment. Likewise when nextTriggerId is null the column
        // is nulled, so re-saving without a chain is a clean re-arm.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var pointerToWrite = newPendingTillUtc.HasValue ? nextTriggerId : null;
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tickets
               SET pending_till_utc = @newPendingTillUtc,
                   pending_till_next_trigger_id = @pointerToWrite,
                   updated_utc = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """,
            new { newPendingTillUtc, pointerToWrite, ticketId }, tx, cancellationToken: ct));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var metadata = TriggerEventMetadata.SystemNote(triggerId, new Dictionary<string, object?>
        {
            ["change"] = "pending_till_utc",
            ["to"] = newPendingTillUtc,
            ["nextTriggerId"] = pointerToWrite,
        });

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal,
                                        body_text)
            VALUES (@ticketId, 'SystemNote', NULL, @metadata::jsonb, TRUE,
                    @bodyText)
            """,
            new
            {
                ticketId,
                metadata,
                bodyText = newPendingTillUtc.HasValue
                    ? $"Pending until {newPendingTillUtc.Value:O} (set by trigger)."
                    : "Pending-till cleared by trigger.",
            }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return true;
    }

    /// Clears the chained-trigger pointer without touching pending_till_utc.
    /// Called by <see cref="TriggerService"/> after a chained reminder
    /// trigger has been dispatched so the next pending-cycle re-arms
    /// explicitly. We deliberately do NOT clear pending_till_utc here —
    /// the chained trigger is allowed to set its own pending-till
    /// downstream, or leave it expired so the agent sees the elapsed
    /// state.
    public async Task ClearPendingTillNextTriggerAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tickets
               SET pending_till_next_trigger_id = NULL
             WHERE id = @ticketId
               AND pending_till_next_trigger_id IS NOT NULL
            """,
            new { ticketId }, cancellationToken: ct));
    }
}

internal sealed record FieldChangeOutcome(
    FieldChangeStatus Status,
    string Column,
    Guid? From = null,
    Guid? To = null,
    string? FromName = null,
    string? ToName = null,
    string? Reason = null)
{
    public static FieldChangeOutcome Applied(string column, Guid? from, Guid to, string? fromName, string? toName)
        => new(FieldChangeStatus.Applied, column, from, to, fromName, toName);

    public static FieldChangeOutcome AlreadyAtTarget(string column)
        => new(FieldChangeStatus.NoOp, column, Reason: "Field already at target value.");

    public static FieldChangeOutcome TargetNotFound(string column, Guid target)
        => new(FieldChangeStatus.Failed, column, To: target,
               Reason: $"Target id {target} not found in lookup table.");

    public static FieldChangeOutcome TicketGone(string column)
        => new(FieldChangeStatus.Failed, column, Reason: "Ticket vanished mid-update.");
}

internal enum FieldChangeStatus
{
    Applied,
    NoOp,
    Failed,
}
