namespace Servicedesk.Infrastructure.Triggers;

public interface ITriggerService
{
    /// Evaluates the active <see cref="TriggerActivatorKind.Action"/>
    /// triggers against the ticket that just changed. Always-mode
    /// triggers run unconditionally; Selective-mode triggers
    /// short-circuit when none of their referenced fields appear in
    /// <paramref name="changeSet"/> (and no article was added).
    ///
    /// Safe to call after every ticket-mutation: an empty trigger table
    /// is a single indexed query and a no-op return. Loop-capped via
    /// <see cref="TriggerLoopGuard"/> so a faulty handler that re-enters
    /// the evaluator can't spin indefinitely. Failures inside one trigger
    /// don't stop subsequent triggers in the same pass — the evaluator
    /// records the failure on the trigger's run row and continues.
    Task EvaluateAsync(
        Guid ticketId,
        long? ticketEventId,
        TriggerActivatorKind activatorKind,
        TriggerChangeSet changeSet,
        CancellationToken ct);

    /// Scheduler-driven single-pair evaluation (Blok 5). Called by
    /// <c>TriggerSchedulerWorker</c> per (trigger, ticket) candidate
    /// row. <paramref name="boundaryUtc"/> is the temporal moment that
    /// just elapsed (pending-till, SLA deadline, deadline minus warning
    /// offset) — recorded on the <c>trigger_runs</c> row's
    /// <c>applied_changes</c> JSON so the run-history page can show
    /// "fired because deadline T was crossed". The matcher still runs:
    /// a time-trigger with conditions (e.g. only escalate priority=high)
    /// short-circuits when conditions don't match and writes a
    /// <c>skipped_no_match</c> row instead.
    ///
    /// <paramref name="expectedActivatorMode"/> is what the scheduler
    /// expected this trigger to be (<c>reminder</c>, <c>escalation</c>,
    /// <c>escalation_warning</c>) — defends against the race where the
    /// admin re-typed the trigger between the candidate scan and
    /// dispatch (or where a chained pending-till pointer was set when
    /// the target was time:reminder but has since been re-typed). On
    /// mismatch the evaluator writes a Failed run row and returns
    /// without invoking handlers.
    ///
    /// Returns the outcome the evaluator persisted (or null when the
    /// trigger or ticket disappeared mid-pass and no row was written).
    /// The scheduler uses this to decide whether to clear chained-
    /// reminder pointer state — chained reminders only release the
    /// pointer on Applied/Failed so a SkippedNoMatch can re-evaluate
    /// when the ticket changes shape.
    Task<TriggerRunOutcome?> EvaluateScheduledAsync(
        Guid triggerId,
        Guid ticketId,
        DateTime boundaryUtc,
        string expectedActivatorMode,
        CancellationToken ct);

    /// Admin test-runner (Blok 7). Evaluates a single trigger against a
    /// real ticket using the production matcher + render-context factory,
    /// but routes actions through the read-only previewer dispatcher so
    /// nothing is mutated, no mail is sent, and no <c>trigger_runs</c>
    /// row is written. Returns null when the trigger or ticket is not
    /// found; otherwise returns a diff-shape that the UI renders as
    /// "this is what would happen on this ticket".
    Task<TriggerDryRunResult?> DryRunAsync(
        Guid triggerId,
        Guid ticketId,
        CancellationToken ct);
}

public sealed record TriggerDryRunResult(
    bool Matched,
    string? FailureReason,
    IReadOnlyList<TriggerActionPreviewResult> Actions);
