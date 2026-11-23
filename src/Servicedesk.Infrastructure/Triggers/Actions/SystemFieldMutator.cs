using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// The trigger-only reminder writes: <c>set_pending_till</c> and the
/// scheduler's clean-up of an elapsed or chained reminder. Field changes
/// (queue, status, priority, category, owner) do not live here any more —
/// they go through the shared <see cref="Persistence.Tickets.TicketFieldWriter"/>
/// via <see cref="TriggerFieldWrite"/>, the same write path as an agent
/// (v0.1.65, ROADMAP §2 C3). These stay separate because the chained-trigger
/// pointer and the optimistic boundary guards exist only on the trigger side.
internal sealed class SystemFieldMutator
{
    private readonly NpgsqlDataSource _dataSource;

    public SystemFieldMutator(NpgsqlDataSource dataSource) => _dataSource = dataSource;

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

    /// Clears BOTH <c>pending_till_utc</c> and the chained-trigger
    /// pointer once a chained reminder has run on Applied/Failed —
    /// stops the wide-scan from picking the ticket back up on the next
    /// tick, which was the leak before v0.0.24 fix batch 2 (pointer
    /// cleared, pending_till still elapsed, wide branch re-fired every
    /// reminder trigger). The optimistic <c>WHERE</c> guard preserves a
    /// re-arm: if the chained trigger's actions reset pending_till_utc
    /// to a new boundary (or pointed to a new chain), the row no longer
    /// matches the original (boundary, pointer) and the clear is a
    /// no-op — the trigger's re-arm wins.
    public async Task ClearChainedReminderStateAsync(
        Guid ticketId, Guid triggerId, DateTime boundaryUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tickets
               SET pending_till_utc = NULL,
                   pending_till_next_trigger_id = NULL,
                   updated_utc = now()
             WHERE id = @ticketId
               AND pending_till_next_trigger_id = @triggerId
               AND pending_till_utc = @boundaryUtc
            """,
            new { ticketId, triggerId, boundaryUtc }, cancellationToken: ct));
    }

    /// v0.0.100 — wide-branch counterpart of
    /// <see cref="ClearChainedReminderStateAsync"/>: once a tick has
    /// evaluated every reminder trigger against an elapsed ticket and
    /// none of them applied, the elapsed <c>pending_till_utc</c> is
    /// wiped so the ticket stops being a candidate. Before this the
    /// scheduler re-loaded and re-evaluated such tickets every tick
    /// indefinitely (a "skipped_no_match" run row per minute per ticket
    /// — 1.5 GB of trigger_runs on one install). The optimistic
    /// <c>pending_till_utc = @boundaryUtc</c> guard preserves any re-arm
    /// that happened in between (agent or trigger set a new value).
    /// Returns true when the row was actually cleared.
    public async Task<bool> ClearElapsedReminderAsync(
        Guid ticketId, DateTime boundaryUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tickets
               SET pending_till_utc = NULL,
                   pending_till_next_trigger_id = NULL,
                   updated_utc = now()
             WHERE id = @ticketId
               AND pending_till_utc = @boundaryUtc
            """,
            new { ticketId, boundaryUtc }, cancellationToken: ct));
        return affected > 0;
    }
}
