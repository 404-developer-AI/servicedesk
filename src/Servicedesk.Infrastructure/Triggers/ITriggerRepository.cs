namespace Servicedesk.Infrastructure.Triggers;

public interface ITriggerRepository
{
    /// Returns active triggers for the given activator-kind, ordered
    /// alphabetically by lower(name) — admins prefix with <c>010-</c>,
    /// <c>020-</c>, … when fine-grained ordering matters (Zammad
    /// convention; see TRIGGERS.md §5).
    Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(
        TriggerActivatorKind activatorKind,
        CancellationToken ct);

    /// Admin-scope list — every row regardless of <c>is_active</c>.
    /// Ordered by <c>lower(name)</c> so the UI mirrors evaluation order.
    Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct);

    /// Single-trigger lookup used by the scheduler's per-pair evaluation
    /// path. Returns null when the trigger was deleted or deactivated
    /// between the candidate scan and the dispatch.
    Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct);

    /// Admin CRUD — Blok 6. The repo trusts the caller to have validated
    /// JSON shape + activator-kind/mode coherence; SQL only enforces the
    /// CHECK-constraints declared at bootstrap time.
    Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct);
    Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct);
    Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// Aggregate <c>trigger_runs</c> over the rolling 24h window for the
    /// list view. One row per trigger that has at least one run; missing
    /// trigger ids in the result mean "no runs since cutoff" — the API
    /// presents that as zero.
    Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(
        DateTime sinceUtc, CancellationToken ct);

    /// Per-trigger paginated run history for <c>/settings/triggers/{id}/runs</c>.
    /// Uses fired_utc DESC; <paramref name="cursorUtc"/> + matching id for
    /// stable forward pagination (Blok 8 surfaces this; Blok 6 ships the repo).
    Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(
        Guid triggerId, int limit, DateTime? cursorUtc, CancellationToken ct);

    /// Appends one row to <c>trigger_runs</c>. Called for every evaluator
    /// pass — including no-match / loop-skip outcomes — so the admin UI
    /// can show "this trigger never fires" alongside "this trigger fires
    /// 200×/day".
    Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct);

    /// Scheduler scan — Blok 5. Returns (ticket, trigger) pairs whose
    /// <c>tickets.pending_till_utc</c> has elapsed and that have not yet
    /// fired an applied/failed run since that boundary. Pairs are deduped
    /// at the SQL layer so the worker iterates at most once per boundary.
    Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(
        int limit, CancellationToken ct);

    /// Scheduler scan — Blok 5. Returns (ticket, trigger) pairs whose
    /// SLA first-response or resolution deadline has elapsed without
    /// being met. Each unmet deadline yields its own row, so a single
    /// ticket past both deadlines fires the trigger once for each.
    Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(
        int limit, CancellationToken ct);

    /// Scheduler scan — Blok 5. Returns (ticket, trigger) pairs whose
    /// SLA deadline minus <paramref name="warningMinutes"/> has elapsed
    /// but the deadline itself is still in the future. The boundary is
    /// (deadline − warningMinutes); dedup is per-boundary so a single
    /// recompute that shifts the deadline forward gets a fresh warning
    /// row only when the new warning moment also lands in the past.
    Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(
        int warningMinutes, int limit, CancellationToken ct);
}

/// What the evaluator persists to the <c>trigger_runs</c> append-only
/// audit table after a single trigger pass on a single ticket.
public sealed record TriggerRunRecord(
    Guid TriggerId,
    Guid TicketId,
    long? TicketEventId,
    TriggerRunOutcome Outcome,
    string? AppliedChangesJson,
    string? ErrorClass,
    string? ErrorMessage);

/// One (ticket, trigger) pair surfaced by the scheduler's candidate scan.
/// <see cref="BoundaryUtc"/> is the temporal moment that just elapsed —
/// pending_till for reminders, the SLA deadline for escalations, the
/// deadline minus the warning offset for warnings. The scheduler passes
/// the boundary into <c>EvaluateScheduledAsync</c> so the audit row
/// records what time was crossed (helps admins read the run-history).
/// Row-DTO for the scheduler's candidate scans. Sealed class with
/// auto-properties (per the project's Dapper convention) so Dapper
/// can hydrate it via column-alias matching even when individual
/// queries only project a subset of the columns — the escalation +
/// escalation_warning scans don't need <see cref="IsChainedReminder"/>
/// and leave it at the default false.
public sealed class TriggerScheduleCandidate
{
    public Guid TicketId { get; set; }
    public Guid TriggerId { get; set; }
    public DateTime BoundaryUtc { get; set; }
    public bool IsChainedReminder { get; set; }
}

/// Insert payload for <see cref="ITriggerRepository.CreateAsync"/>. The
/// shape mirrors <see cref="TriggerRow"/> minus the server-assigned
/// audit columns (id / created_utc / updated_utc).
public sealed record NewTrigger(
    string Name,
    string Description,
    bool IsActive,
    string ActivatorKind,
    string ActivatorMode,
    string ConditionsJson,
    string ActionsJson,
    string? Locale,
    string? Timezone,
    string Note,
    Guid? CreatedByUserId);

/// Update payload — <see cref="CreatedByUserId"/> is intentionally absent;
/// the original creator is preserved.
public sealed record UpdateTrigger(
    string Name,
    string Description,
    bool IsActive,
    string ActivatorKind,
    string ActivatorMode,
    string ConditionsJson,
    string ActionsJson,
    string? Locale,
    string? Timezone,
    string Note);

/// Aggregate over a trigger's <c>trigger_runs</c> rows since a given UTC
/// cutoff. <see cref="LastFiredUtc"/> is the most recent fired_utc across
/// all outcomes so the list view can show "last fired N min ago".
public sealed class TriggerRunSummary
{
    public Guid TriggerId { get; set; }
    public int AppliedCount { get; set; }
    public int SkippedNoMatchCount { get; set; }
    public int SkippedLoopCount { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LastFiredUtc { get; set; }
}

/// Single row from <c>trigger_runs</c>, denormalised for the run-history
/// table. <see cref="TicketNumber"/> is joined on the spot so the table
/// can link directly to <c>/tickets/{number}</c> without a per-row roundtrip.
public sealed class TriggerRunDetail
{
    public Guid Id { get; set; }
    public Guid TriggerId { get; set; }
    public Guid TicketId { get; set; }
    public long? TicketNumber { get; set; }
    public long? TicketEventId { get; set; }
    public DateTime FiredUtc { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? AppliedChangesJson { get; set; }
    public string? ErrorClass { get; set; }
    public string? ErrorMessage { get; set; }
}
