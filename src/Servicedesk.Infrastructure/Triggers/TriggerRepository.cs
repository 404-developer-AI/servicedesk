using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Triggers;

public sealed class TriggerRepository : ITriggerRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TriggerRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(
        TriggerActivatorKind activatorKind, CancellationToken ct)
    {
        // Every column carries an AS PascalCase alias so Dapper hydrates
        // the sealed-class DTO without underscore-matching. JSONB columns
        // are cast to text so we can hold them as strings and parse with
        // System.Text.Json on the C# side.
        const string sql = """
            SELECT id                  AS Id,
                   name                AS Name,
                   description         AS Description,
                   is_active           AS IsActive,
                   activator_kind      AS ActivatorKind,
                   activator_mode      AS ActivatorMode,
                   conditions::text    AS ConditionsJson,
                   actions::text       AS ActionsJson,
                   locale              AS Locale,
                   timezone            AS Timezone,
                   note                AS Note,
                   created_utc         AS CreatedUtc,
                   updated_utc         AS UpdatedUtc,
                   created_by_user_id  AS CreatedByUserId
            FROM triggers
            WHERE is_active = TRUE
              AND activator_kind = @ActivatorKind
            ORDER BY lower(name)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerRow>(new CommandDefinition(
            sql, new { ActivatorKind = ActivatorKindToDb(activatorKind) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id                  AS Id,
                   name                AS Name,
                   description         AS Description,
                   is_active           AS IsActive,
                   activator_kind      AS ActivatorKind,
                   activator_mode      AS ActivatorMode,
                   conditions::text    AS ConditionsJson,
                   actions::text       AS ActionsJson,
                   locale              AS Locale,
                   timezone            AS Timezone,
                   note                AS Note,
                   created_utc         AS CreatedUtc,
                   updated_utc         AS UpdatedUtc,
                   created_by_user_id  AS CreatedByUserId
            FROM triggers
            ORDER BY lower(name)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct)
    {
        const string sql = """
            SELECT id                  AS Id,
                   name                AS Name,
                   description         AS Description,
                   is_active           AS IsActive,
                   activator_kind      AS ActivatorKind,
                   activator_mode      AS ActivatorMode,
                   conditions::text    AS ConditionsJson,
                   actions::text       AS ActionsJson,
                   locale              AS Locale,
                   timezone            AS Timezone,
                   note                AS Note,
                   created_utc         AS CreatedUtc,
                   updated_utc         AS UpdatedUtc,
                   created_by_user_id  AS CreatedByUserId
            FROM triggers
            WHERE id = @triggerId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TriggerRow>(new CommandDefinition(
            sql, new { triggerId }, cancellationToken: ct));
    }

    public async Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO triggers
                (name, description, is_active, activator_kind, activator_mode,
                 conditions, actions, locale, timezone, note, created_by_user_id)
            VALUES
                (@Name, @Description, @IsActive, @ActivatorKind, @ActivatorMode,
                 @ConditionsJson::jsonb, @ActionsJson::jsonb,
                 @Locale, @Timezone, @Note, @CreatedByUserId)
            RETURNING id                  AS Id,
                      name                AS Name,
                      description         AS Description,
                      is_active           AS IsActive,
                      activator_kind      AS ActivatorKind,
                      activator_mode      AS ActivatorMode,
                      conditions::text    AS ConditionsJson,
                      actions::text       AS ActionsJson,
                      locale              AS Locale,
                      timezone            AS Timezone,
                      note                AS Note,
                      created_utc         AS CreatedUtc,
                      updated_utc         AS UpdatedUtc,
                      created_by_user_id  AS CreatedByUserId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<TriggerRow>(new CommandDefinition(sql, row, cancellationToken: ct));
    }

    public async Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct)
    {
        const string sql = """
            UPDATE triggers SET
                name           = @Name,
                description    = @Description,
                is_active      = @IsActive,
                activator_kind = @ActivatorKind,
                activator_mode = @ActivatorMode,
                conditions     = @ConditionsJson::jsonb,
                actions        = @ActionsJson::jsonb,
                locale         = @Locale,
                timezone       = @Timezone,
                note           = @Note,
                is_seed        = FALSE,
                updated_utc    = now()
            WHERE id = @id
            RETURNING id                  AS Id,
                      name                AS Name,
                      description         AS Description,
                      is_active           AS IsActive,
                      activator_kind      AS ActivatorKind,
                      activator_mode      AS ActivatorMode,
                      conditions::text    AS ConditionsJson,
                      actions::text       AS ActionsJson,
                      locale              AS Locale,
                      timezone            AS Timezone,
                      note                AS Note,
                      created_utc         AS CreatedUtc,
                      updated_utc         AS UpdatedUtc,
                      created_by_user_id  AS CreatedByUserId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var parameters = new DynamicParameters(row);
        parameters.Add("id", id);
        return await conn.QueryFirstOrDefaultAsync<TriggerRow>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        const string sql = """
            UPDATE triggers
               SET is_active = @isActive,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, isActive }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        // ON DELETE CASCADE on trigger_runs → no manual cleanup needed.
        const string sql = "DELETE FROM triggers WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(
        DateTime sinceUtc, CancellationToken ct)
    {
        // One scan over the rolling window; the result map is keyed on
        // trigger_id so the API can do a single dictionary-lookup per
        // list row. Triggers without rows in the window are absent —
        // the caller treats that as zero counts.
        const string sql = """
            SELECT trigger_id                                            AS TriggerId,
                   COUNT(*) FILTER (WHERE outcome = 'applied')           AS AppliedCount,
                   COUNT(*) FILTER (WHERE outcome = 'skipped_no_match')  AS SkippedNoMatchCount,
                   COUNT(*) FILTER (WHERE outcome = 'skipped_loop')      AS SkippedLoopCount,
                   COUNT(*) FILTER (WHERE outcome = 'failed')            AS FailedCount,
                   MAX(fired_utc)                                        AS LastFiredUtc
              FROM trigger_runs
             WHERE fired_utc >= @sinceUtc
             GROUP BY trigger_id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerRunSummary>(
            new CommandDefinition(sql, new { sinceUtc }, cancellationToken: ct));
        return rows.ToDictionary(r => r.TriggerId);
    }

    public async Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(
        Guid triggerId, int limit, DateTime? cursorUtc, CancellationToken ct)
    {
        // The (trigger_id, fired_utc desc) index makes the cursor walk
        // O(log n) per page. We pass the cursor as a strict less-than
        // bound so the boundary row appears exactly once across page
        // requests, even though fired_utc can repeat at scheduler-tick
        // resolution.
        const string sql = """
            SELECT r.id                AS Id,
                   r.trigger_id        AS TriggerId,
                   r.ticket_id         AS TicketId,
                   t.number            AS TicketNumber,
                   r.ticket_event_id   AS TicketEventId,
                   r.fired_utc         AS FiredUtc,
                   r.outcome           AS Outcome,
                   r.applied_changes::text AS AppliedChangesJson,
                   r.error_class       AS ErrorClass,
                   r.error_message     AS ErrorMessage
              FROM trigger_runs r
              LEFT JOIN tickets t ON t.id = r.ticket_id
             WHERE r.trigger_id = @triggerId
               AND (@cursorUtc::timestamptz IS NULL OR r.fired_utc < @cursorUtc)
             ORDER BY r.fired_utc DESC
             LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerRunDetail>(
            new CommandDefinition(sql, new { triggerId, cursorUtc, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO trigger_runs
                (trigger_id, ticket_id, ticket_event_id, outcome,
                 applied_changes, error_class, error_message)
            VALUES
                (@TriggerId, @TicketId, @TicketEventId, @Outcome,
                 @AppliedChangesJson::jsonb, @ErrorClass, @ErrorMessage)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            record.TriggerId,
            record.TicketId,
            record.TicketEventId,
            Outcome = OutcomeToDb(record.Outcome),
            record.AppliedChangesJson,
            record.ErrorClass,
            record.ErrorMessage,
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(
        int limit, CancellationToken ct)
    {
        // Two streams unioned:
        //
        // (A) Chained tickets — pending_till_next_trigger_id is set AND
        //     the target trigger is currently active. The scheduler
        //     fires ONLY that trigger for that ticket on this
        //     pending-cycle; other reminder triggers skip the ticket
        //     until the pointer clears (handled in TriggerService after
        //     the chained run). This is the user-facing "when this
        //     specific pending-till elapses, run trigger Y" feature.
        //
        // (B) Wide reminder scan — tickets without a pointer, OR with a
        //     pointer to an INACTIVE trigger, fall back to the cross-
        //     join behaviour: every active time:reminder trigger is
        //     evaluated. Without the inactive-fallback, deactivating a
        //     chain target leaves any in-flight tickets pointing at it
        //     stranded — neither branch matches, pending_till_utc never
        //     clears, no reminder ever fires. Re-activating the trigger
        //     restores the chained branch automatically.
        //
        // Both streams share the same dedup predicate (NOT EXISTS against
        // applied/failed run rows past the boundary) so a chained trigger
        // also can't double-fire on a single boundary.
        const string sql = """
            (
                SELECT t.id              AS TicketId,
                       tr.id             AS TriggerId,
                       t.pending_till_utc AS BoundaryUtc,
                       TRUE              AS IsChainedReminder
                FROM tickets t
                JOIN triggers tr ON tr.id = t.pending_till_next_trigger_id
                WHERE t.is_deleted = FALSE
                  AND t.pending_till_utc IS NOT NULL
                  AND t.pending_till_utc <= now()
                  AND t.pending_till_next_trigger_id IS NOT NULL
                  AND tr.is_active = TRUE
                  AND NOT EXISTS (
                    SELECT 1 FROM trigger_runs tre
                    WHERE tre.trigger_id = tr.id
                      AND tre.ticket_id = t.id
                      AND tre.outcome IN ('applied','failed')
                      AND tre.fired_utc >= t.pending_till_utc
                  )
            )
            UNION ALL
            (
                SELECT t.id              AS TicketId,
                       tr.id             AS TriggerId,
                       t.pending_till_utc AS BoundaryUtc,
                       FALSE             AS IsChainedReminder
                FROM tickets t
                CROSS JOIN triggers tr
                WHERE t.is_deleted = FALSE
                  AND t.pending_till_utc IS NOT NULL
                  AND t.pending_till_utc <= now()
                  AND (
                        t.pending_till_next_trigger_id IS NULL
                        OR NOT EXISTS (
                            SELECT 1 FROM triggers tr_target
                            WHERE tr_target.id = t.pending_till_next_trigger_id
                              AND tr_target.is_active = TRUE
                        )
                  )
                  AND tr.is_active = TRUE
                  AND tr.activator_kind = 'time'
                  AND tr.activator_mode = 'reminder'
                  AND NOT EXISTS (
                    SELECT 1 FROM trigger_runs tre
                    WHERE tre.trigger_id = tr.id
                      AND tre.ticket_id = t.id
                      AND tre.outcome IN ('applied','failed')
                      AND tre.fired_utc >= t.pending_till_utc
                  )
            )
            ORDER BY BoundaryUtc
            LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerScheduleCandidate>(new CommandDefinition(
            sql, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(
        int limit, CancellationToken ct)
    {
        // UNION ALL across the two SLA deadlines: a ticket past both
        // first-response and resolution yields two pairs (and the trigger
        // fires twice). The matcher decides if the trigger's conditions
        // still apply at the moment of dispatch.
        const string sql = """
            SELECT t.id  AS TicketId,
                   tr.id AS TriggerId,
                   s.first_response_deadline_utc AS BoundaryUtc
            FROM tickets t
            JOIN ticket_sla_state s ON s.ticket_id = t.id
            CROSS JOIN triggers tr
            WHERE t.is_deleted = FALSE
              AND s.first_response_deadline_utc IS NOT NULL
              AND s.first_response_met_utc IS NULL
              AND s.first_response_deadline_utc <= now()
              AND tr.is_active = TRUE
              AND tr.activator_kind = 'time'
              AND tr.activator_mode = 'escalation'
              AND NOT EXISTS (
                SELECT 1 FROM trigger_runs tre
                WHERE tre.trigger_id = tr.id
                  AND tre.ticket_id = t.id
                  AND tre.outcome IN ('applied','failed')
                  AND tre.fired_utc >= s.first_response_deadline_utc
              )
            UNION ALL
            SELECT t.id  AS TicketId,
                   tr.id AS TriggerId,
                   s.resolution_deadline_utc AS BoundaryUtc
            FROM tickets t
            JOIN ticket_sla_state s ON s.ticket_id = t.id
            CROSS JOIN triggers tr
            WHERE t.is_deleted = FALSE
              AND s.resolution_deadline_utc IS NOT NULL
              AND s.resolution_met_utc IS NULL
              AND s.resolution_deadline_utc <= now()
              AND tr.is_active = TRUE
              AND tr.activator_kind = 'time'
              AND tr.activator_mode = 'escalation'
              AND NOT EXISTS (
                SELECT 1 FROM trigger_runs tre
                WHERE tre.trigger_id = tr.id
                  AND tre.ticket_id = t.id
                  AND tre.outcome IN ('applied','failed')
                  AND tre.fired_utc >= s.resolution_deadline_utc
              )
            ORDER BY BoundaryUtc
            LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerScheduleCandidate>(new CommandDefinition(
            sql, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(
        int warningMinutes, int limit, CancellationToken ct)
    {
        // Warning fires in the (deadline − warningMinutes) → deadline
        // window. The deadline-side guard (s.*_deadline_utc > now())
        // prevents retroactive warnings: once the deadline itself is
        // past, the escalation trigger takes over and there is no point
        // shouting "deadline is approaching" anymore.
        const string sql = """
            SELECT t.id  AS TicketId,
                   tr.id AS TriggerId,
                   s.first_response_deadline_utc - (@warningMinutes * INTERVAL '1 minute') AS BoundaryUtc
            FROM tickets t
            JOIN ticket_sla_state s ON s.ticket_id = t.id
            CROSS JOIN triggers tr
            WHERE t.is_deleted = FALSE
              AND s.first_response_deadline_utc IS NOT NULL
              AND s.first_response_met_utc IS NULL
              AND s.first_response_deadline_utc > now()
              AND s.first_response_deadline_utc - (@warningMinutes * INTERVAL '1 minute') <= now()
              AND tr.is_active = TRUE
              AND tr.activator_kind = 'time'
              AND tr.activator_mode = 'escalation_warning'
              AND NOT EXISTS (
                SELECT 1 FROM trigger_runs tre
                WHERE tre.trigger_id = tr.id
                  AND tre.ticket_id = t.id
                  AND tre.outcome IN ('applied','failed')
                  AND tre.fired_utc >= s.first_response_deadline_utc - (@warningMinutes * INTERVAL '1 minute')
              )
            UNION ALL
            SELECT t.id  AS TicketId,
                   tr.id AS TriggerId,
                   s.resolution_deadline_utc - (@warningMinutes * INTERVAL '1 minute') AS BoundaryUtc
            FROM tickets t
            JOIN ticket_sla_state s ON s.ticket_id = t.id
            CROSS JOIN triggers tr
            WHERE t.is_deleted = FALSE
              AND s.resolution_deadline_utc IS NOT NULL
              AND s.resolution_met_utc IS NULL
              AND s.resolution_deadline_utc > now()
              AND s.resolution_deadline_utc - (@warningMinutes * INTERVAL '1 minute') <= now()
              AND tr.is_active = TRUE
              AND tr.activator_kind = 'time'
              AND tr.activator_mode = 'escalation_warning'
              AND NOT EXISTS (
                SELECT 1 FROM trigger_runs tre
                WHERE tre.trigger_id = tr.id
                  AND tre.ticket_id = t.id
                  AND tre.outcome IN ('applied','failed')
                  AND tre.fired_utc >= s.resolution_deadline_utc - (@warningMinutes * INTERVAL '1 minute')
              )
            ORDER BY BoundaryUtc
            LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerScheduleCandidate>(new CommandDefinition(
            sql, new { warningMinutes, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public static string ActivatorKindToDb(TriggerActivatorKind kind) => kind switch
    {
        TriggerActivatorKind.Action => "action",
        TriggerActivatorKind.Time => "time",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string OutcomeToDb(TriggerRunOutcome outcome) => outcome switch
    {
        TriggerRunOutcome.Applied => "applied",
        TriggerRunOutcome.SkippedNoMatch => "skipped_no_match",
        TriggerRunOutcome.SkippedLoop => "skipped_loop",
        TriggerRunOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}
