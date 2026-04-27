using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers;

/// Evaluator core for v0.0.24 triggers (Blok 2). Walks the active
/// triggers, applies condition matching, dispatches actions, and writes
/// an append-only audit row to <c>trigger_runs</c>. Designed to fail
/// open: a malformed JSON column or a missing handler is logged and the
/// pass continues — the original ticket-mutation must not be rolled back
/// by trigger faults.
public sealed class TriggerService : ITriggerService
{
    private readonly ITriggerRepository _repo;
    private readonly ITicketRepository _tickets;
    private readonly ITriggerConditionMatcher _matcher;
    private readonly ITriggerActionDispatcher _dispatcher;
    private readonly ITriggerActionPreviewDispatcher _previewDispatcher;
    private readonly TriggerLoopGuard _loopGuard;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly ITriggerRenderContextFactory _renderFactory;
    private readonly Realtime.ITicketListNotifier _listNotifier;
    private readonly ILogger<TriggerService> _logger;

    public TriggerService(
        ITriggerRepository repo,
        ITicketRepository tickets,
        ITriggerConditionMatcher matcher,
        ITriggerActionDispatcher dispatcher,
        ITriggerActionPreviewDispatcher previewDispatcher,
        TriggerLoopGuard loopGuard,
        ISettingsService settings,
        IAuditLogger audit,
        ITriggerRenderContextFactory renderFactory,
        Realtime.ITicketListNotifier listNotifier,
        ILogger<TriggerService> logger)
    {
        _repo = repo;
        _tickets = tickets;
        _matcher = matcher;
        _dispatcher = dispatcher;
        _previewDispatcher = previewDispatcher;
        _loopGuard = loopGuard;
        _settings = settings;
        _audit = audit;
        _renderFactory = renderFactory;
        _listNotifier = listNotifier;
        _logger = logger;
    }

    public async Task EvaluateAsync(
        Guid ticketId,
        long? ticketEventId,
        TriggerActivatorKind activatorKind,
        TriggerChangeSet changeSet,
        CancellationToken ct)
    {
        var maxChain = await _settings.GetAsync<int>(SettingKeys.Triggers.MaxChainPerMutation, ct);
        if (maxChain <= 0) maxChain = 10;

        if (_loopGuard.Depth >= maxChain)
        {
            // Re-entrant trigger pass exceeded the chain cap. Per
            // TRIGGERS.md §5 this is a safety-net for buggy handlers
            // that mutate state through paths that re-enter the
            // evaluator. Record one SkippedLoop row per active trigger
            // so admins can see in run-history which triggers were
            // suppressed by the cap (UI/DB already know the outcome).
            _logger.LogWarning(
                "Trigger evaluation skipped on ticket {TicketId}: chain depth {Depth} reached cap {Cap}.",
                ticketId, _loopGuard.Depth, maxChain);
            await RecordChainCapSkipAsync(ticketId, ticketEventId, activatorKind, ct);
            return;
        }

        using var scope = _loopGuard.Enter();

        IReadOnlyList<TriggerRow> triggers;
        try
        {
            triggers = await _repo.LoadActiveAsync(activatorKind, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trigger evaluator failed to load active triggers for ticket {TicketId}.", ticketId);
            return;
        }
        if (triggers.Count == 0) return;

        TicketDetail? detail = null;
        TicketEvent? triggeringEvent = null;

        foreach (var trigger in triggers)
        {
            if (!Enum.TryParse<TriggerActivatorMode>(trigger.ActivatorMode, ignoreCase: true, out var mode))
            {
                _logger.LogWarning("Trigger {TriggerId} has unknown activator_mode '{Mode}'; skipping.",
                    trigger.Id, trigger.ActivatorMode);
                continue;
            }

            // Selective short-circuit happens BEFORE any JSON parsing or
            // ticket fetch — the cheapest possible noop for triggers whose
            // referenced fields didn't change. The matcher's referenced-
            // fields scan is on a tiny conditions JSON so it's effectively
            // free. We still parse the conditions doc to get there; if it
            // is malformed we fall through to the catch block which records
            // the failure once, just like the per-trigger run loop below.
            if (mode == TriggerActivatorMode.Selective && !changeSet.ArticleAdded)
            {
                JsonDocument? cheap = null;
                try
                {
                    cheap = JsonDocument.Parse(trigger.ConditionsJson);
                    var refs = _matcher.ReferencedFields(cheap.RootElement);
                    var anyMatch = false;
                    foreach (var f in refs)
                    {
                        if (changeSet.ChangedFields.Contains(f)) { anyMatch = true; break; }
                    }
                    if (!anyMatch) continue;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Trigger {TriggerId} has invalid conditions JSON; skipping.", trigger.Id);
                    await SafeRecordAsync(new TriggerRunRecord(
                        trigger.Id, ticketId, ticketEventId,
                        TriggerRunOutcome.Failed, null,
                        nameof(JsonException), ex.Message), ct);
                    continue;
                }
                finally { cheap?.Dispose(); }
            }

            detail ??= await _tickets.GetByIdAsync(ticketId, ct);
            if (detail is null)
            {
                _logger.LogWarning("Ticket {TicketId} disappeared mid-evaluation; aborting trigger pass.", ticketId);
                return;
            }
            if (triggeringEvent is null && ticketEventId.HasValue)
            {
                triggeringEvent = detail.Events.FirstOrDefault(e => e.Id == ticketEventId.Value);
            }

            var (_, results) = await RunOneTriggerAsync(
                trigger, detail.Ticket, triggeringEvent,
                ticketEventId, changeSet, scheduledBoundaryUtc: null, ct);

            var applied = results.Any(r => r.Status == TriggerActionStatus.Applied);
            if (applied)
            {
                // Realtime fan-out so an open detail page or list view
                // refreshes without an F5. The HTTP endpoint that started
                // this evaluator pass already broadcast once for its own
                // mutation; this second notify covers the trigger's
                // additional changes (status set by trigger, etc.).
                await SafeNotifyAsync(ticketId, ct);

                // Propagate this trigger's applied field-changes into the
                // running ChangeSet so a later trigger in the alphabetical
                // order can react via has_changed. Without this, the user-
                // visible scenario "trigger A sets status to WFC, trigger
                // B reacts with has_changed(status)" silently dies because
                // the ChangeSet captured the original mutation only.
                changeSet = MergeAppliedFieldsIntoChangeSet(changeSet, results);

                // After actions run, refetch the ticket so the next
                // trigger in the alphabetical list sees the new state.
                // Without this, two cooperating triggers (set_priority
                // then condition-on-priority) wouldn't compose.
                detail = await _tickets.GetByIdAsync(ticketId, ct);
                if (detail is null)
                {
                    _logger.LogWarning("Ticket {TicketId} disappeared after trigger {TriggerId} applied actions.",
                        ticketId, trigger.Id);
                    return;
                }
                triggeringEvent = ticketEventId.HasValue
                    ? detail.Events.FirstOrDefault(e => e.Id == ticketEventId.Value)
                    : null;
            }
        }
    }

    /// Maps the action kinds that just applied to the field keys those kinds
    /// mutate, then unions them into the existing ChangeSet's ChangedFields.
    /// Note-/mail-emitting kinds don't flip ArticleAdded — propagating the
    /// article signal would risk surprising cascade behaviour (a trigger that
    /// sends a system mail would then re-fire every selective trigger
    /// matching <c>article.*</c> conditions). Field-changes are the only
    /// kind we propagate today.
    private static TriggerChangeSet MergeAppliedFieldsIntoChangeSet(
        TriggerChangeSet current, IReadOnlyList<TriggerActionResult> results)
    {
        HashSet<string>? merged = null;
        foreach (var r in results)
        {
            if (r.Status != TriggerActionStatus.Applied) continue;
            var fieldKey = ActionKindToFieldKey(r.Kind);
            if (fieldKey is null) continue;
            if (current.ChangedFields.Contains(fieldKey)) continue;
            merged ??= new HashSet<string>(current.ChangedFields, StringComparer.OrdinalIgnoreCase);
            merged.Add(fieldKey);
        }
        return merged is null ? current : new TriggerChangeSet(merged, current.ArticleAdded);
    }

    private static string? ActionKindToFieldKey(string kind) => kind switch
    {
        "set_queue" => TriggerFieldKeys.TicketQueueId,
        "set_priority" => TriggerFieldKeys.TicketPriorityId,
        "set_status" => TriggerFieldKeys.TicketStatusId,
        "set_owner" => TriggerFieldKeys.TicketOwnerId,
        // set_pending_till has no exposed condition-field key today (the
        // catalog doesn't surface pending_till as a trigger condition), so
        // there is nothing to propagate. Notes / mail don't change ticket
        // fields.
        _ => null,
    };

    public async Task<TriggerRunOutcome?> EvaluateScheduledAsync(
        Guid triggerId,
        Guid ticketId,
        DateTime boundaryUtc,
        string expectedActivatorMode,
        CancellationToken ct)
    {
        // Loop guard still applies: a time-trigger that mutates the ticket
        // re-enters the action-kind evaluator via TicketEndpoints, and
        // that pass must respect the same per-mutation chain cap.
        var maxChain = await _settings.GetAsync<int>(SettingKeys.Triggers.MaxChainPerMutation, ct);
        if (maxChain <= 0) maxChain = 10;

        if (_loopGuard.Depth >= maxChain)
        {
            _logger.LogWarning(
                "Scheduled trigger {TriggerId} skipped on ticket {TicketId}: chain depth {Depth} reached cap {Cap}.",
                triggerId, ticketId, _loopGuard.Depth, maxChain);
            await SafeRecordAsync(new TriggerRunRecord(
                triggerId, ticketId, TicketEventId: null,
                TriggerRunOutcome.SkippedLoop, null, null, null), ct);
            return TriggerRunOutcome.SkippedLoop;
        }

        using var scope = _loopGuard.Enter();

        TriggerRow? trigger;
        try
        {
            trigger = await _repo.GetByIdAsync(triggerId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trigger evaluator failed to load trigger {TriggerId}.", triggerId);
            return null;
        }

        if (trigger is null || !trigger.IsActive)
        {
            // Race: trigger was deactivated/deleted between the candidate
            // scan and the dispatch. No trigger_run row to write — there
            // is nothing left to attach it to.
            _logger.LogDebug("Scheduled trigger {TriggerId} no longer active; skipping.", triggerId);
            return null;
        }

        // Defensive activator-pair check — closes the gap where an admin
        // re-typed the trigger between candidate scan and dispatch (e.g.
        // a chained reminder pointer that now resolves to an action-kind
        // trigger). Without this, the trigger's actions would fire
        // outside their designed activator path. We record Failed so the
        // run-history surfaces the misconfiguration, then return Failed
        // so the scheduler clears any chained pointer pointing here.
        if (trigger.ActivatorKind != "time" || trigger.ActivatorMode != expectedActivatorMode)
        {
            _logger.LogWarning(
                "Scheduled trigger {TriggerId} skipped on ticket {TicketId}: expected time:{Expected} but trigger is {Kind}:{Mode}.",
                triggerId, ticketId, expectedActivatorMode, trigger.ActivatorKind, trigger.ActivatorMode);
            await SafeRecordAsync(new TriggerRunRecord(
                triggerId, ticketId, TicketEventId: null,
                TriggerRunOutcome.Failed, null,
                "ActivatorMismatch",
                $"Expected time:{expectedActivatorMode} but trigger is {trigger.ActivatorKind}:{trigger.ActivatorMode}."), ct);
            return TriggerRunOutcome.Failed;
        }

        var detail = await _tickets.GetByIdAsync(ticketId, ct);
        if (detail is null)
        {
            _logger.LogWarning("Ticket {TicketId} disappeared before scheduled trigger evaluation.", ticketId);
            return null;
        }

        var (outcome, results) = await RunOneTriggerAsync(
            trigger, detail.Ticket, triggeringEvent: null,
            ticketEventId: null, TriggerChangeSet.Empty,
            scheduledBoundaryUtc: boundaryUtc, ct);

        // Scheduler-driven mutations have no upstream HTTP endpoint to
        // fan out for them — the notifier is the only path that brings
        // an open detail page in sync. Without this, an admin watching
        // the ticket sees the StatusChange event only on F5.
        if (results.Any(r => r.Status == TriggerActionStatus.Applied))
        {
            await SafeNotifyAsync(ticketId, ct);
        }

        return outcome;
    }

    public async Task<TriggerDryRunResult?> DryRunAsync(
        Guid triggerId,
        Guid ticketId,
        CancellationToken ct)
    {
        // Dry-run never enters the loop guard — it doesn't mutate, so it
        // can't trip the chain cap, and the admin must be able to test a
        // trigger from inside a (potentially deeply nested) eval-pass too.
        var trigger = await _repo.GetByIdAsync(triggerId, ct);
        if (trigger is null) return null;

        var detail = await _tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return null;

        JsonDocument? condDoc = null;
        JsonDocument? actionsDoc = null;
        try
        {
            try
            {
                condDoc = JsonDocument.Parse(trigger.ConditionsJson);
                actionsDoc = JsonDocument.Parse(trigger.ActionsJson);
            }
            catch (JsonException ex)
            {
                return new TriggerDryRunResult(
                    Matched: false,
                    FailureReason: $"Trigger has invalid JSON: {ex.Message}",
                    Actions: Array.Empty<TriggerActionPreviewResult>());
            }

            var ctx = new TriggerEvaluationContext(
                detail.Ticket.Id,
                detail.Ticket,
                TriggeringEvent: null,
                TriggerChangeSet.Empty,
                DateTime.UtcNow,
                trigger.Id);

            bool matched;
            try
            {
                matched = _matcher.Matches(condDoc.RootElement, ctx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trigger {TriggerId} dry-run matcher crashed on ticket {TicketId}.", trigger.Id, ticketId);
                return new TriggerDryRunResult(
                    Matched: false,
                    FailureReason: $"Matcher crashed: {ex.GetType().Name}: {ex.Message}",
                    Actions: Array.Empty<TriggerActionPreviewResult>());
            }

            if (!matched)
            {
                return new TriggerDryRunResult(
                    Matched: false,
                    FailureReason: null,
                    Actions: Array.Empty<TriggerActionPreviewResult>());
            }

            var renderCtx = await _renderFactory.BuildAsync(ctx, trigger.Locale, trigger.Timezone, ct);
            var dispatchCtx = ctx with { RenderContext = renderCtx };

            var results = await _previewDispatcher.PreviewAllAsync(actionsDoc.RootElement, dispatchCtx, ct);

            return new TriggerDryRunResult(
                Matched: true,
                FailureReason: null,
                Actions: results);
        }
        finally
        {
            condDoc?.Dispose();
            actionsDoc?.Dispose();
        }
    }

    /// Returns the trigger_runs outcome it persisted plus the dispatched
    /// action results. The action list lets <see cref="EvaluateAsync"/>
    /// propagate field-changes (set_status, set_queue, …) into the
    /// running ChangeSet so a later trigger conditioned on
    /// <c>has_changed</c> can react to what the previous trigger did in
    /// this same pass. The outcome lets <see cref="EvaluateScheduledAsync"/>
    /// signal back to the scheduler whether to clear chained-pointer
    /// state — Failed-vs-SkippedNoMatch can no longer be inferred from
    /// an empty list because both produce no actions.
    private async Task<(TriggerRunOutcome Outcome, IReadOnlyList<TriggerActionResult> Results)> RunOneTriggerAsync(
        TriggerRow trigger,
        Ticket ticket,
        TicketEvent? triggeringEvent,
        long? ticketEventId,
        TriggerChangeSet changeSet,
        DateTime? scheduledBoundaryUtc,
        CancellationToken ct)
    {
        JsonDocument? condDoc = null;
        JsonDocument? actionsDoc = null;
        try
        {
            condDoc = JsonDocument.Parse(trigger.ConditionsJson);
            actionsDoc = JsonDocument.Parse(trigger.ActionsJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Trigger {TriggerId} has invalid JSON; skipping.", trigger.Id);
            await SafeRecordAsync(new TriggerRunRecord(
                trigger.Id, ticket.Id, ticketEventId,
                TriggerRunOutcome.Failed, null,
                nameof(JsonException), ex.Message), ct);
            condDoc?.Dispose();
            actionsDoc?.Dispose();
            return (TriggerRunOutcome.Failed, Array.Empty<TriggerActionResult>());
        }

        try
        {
            var ctx = new TriggerEvaluationContext(
                ticket.Id,
                ticket,
                triggeringEvent,
                changeSet,
                DateTime.UtcNow,
                trigger.Id);

            var matched = _matcher.Matches(condDoc.RootElement, ctx);
            if (!matched)
            {
                await SafeRecordAsync(new TriggerRunRecord(
                    trigger.Id, ticket.Id, ticketEventId,
                    TriggerRunOutcome.SkippedNoMatch, null, null, null), ct);
                return (TriggerRunOutcome.SkippedNoMatch, Array.Empty<TriggerActionResult>());
            }

            var renderCtx = await _renderFactory.BuildAsync(ctx, trigger.Locale, trigger.Timezone, ct);
            var dispatchCtx = ctx with { RenderContext = renderCtx };

            var results = await _dispatcher.DispatchAllAsync(actionsDoc.RootElement, dispatchCtx, ct);

            var hasFailures = results.Any(r =>
                r.Status == TriggerActionStatus.Failed || r.Status == TriggerActionStatus.NoHandler);
            var outcome = hasFailures ? TriggerRunOutcome.Failed : TriggerRunOutcome.Applied;

            var diff = BuildDiff(trigger, results, scheduledBoundaryUtc);
            var diffJson = JsonSerializer.Serialize(diff);

            await SafeRecordAsync(new TriggerRunRecord(
                trigger.Id, ticket.Id, ticketEventId,
                outcome, diffJson, null, null), ct);

            await SafeAuditAsync(new AuditEvent(
                EventType: TriggerAuditEventTypes.Fired,
                Actor: TriggerSystemActor.Name,
                ActorRole: TriggerSystemActor.Role,
                Target: ticket.Id.ToString(),
                Payload: new
                {
                    triggered_by = trigger.Id,
                    trigger_name = trigger.Name,
                    ticket_event_id = ticketEventId,
                    outcome = outcome.ToString().ToLowerInvariant(),
                    actions_count = results.Count,
                    boundary_utc = scheduledBoundaryUtc,
                }), ct);

            foreach (var result in results)
            {
                if (result.Status != TriggerActionStatus.Applied) continue;
                await SafeAuditAsync(new AuditEvent(
                    EventType: TriggerAuditEventTypes.ActionApplied,
                    Actor: TriggerSystemActor.Name,
                    ActorRole: TriggerSystemActor.Role,
                    Target: ticket.Id.ToString(),
                    Payload: new
                    {
                        triggered_by = trigger.Id,
                        action_kind = result.Kind,
                        summary = result.ChangeSummary,
                    }), ct);
            }

            return (outcome, results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trigger {TriggerId} evaluation crashed on ticket {TicketId}.", trigger.Id, ticket.Id);
            await SafeRecordAsync(new TriggerRunRecord(
                trigger.Id, ticket.Id, ticketEventId,
                TriggerRunOutcome.Failed, null, ex.GetType().Name, ex.Message), ct);
            return (TriggerRunOutcome.Failed, Array.Empty<TriggerActionResult>());
        }
        finally
        {
            condDoc?.Dispose();
            actionsDoc?.Dispose();
        }
    }

    private static object BuildDiff(
        TriggerRow trigger,
        IReadOnlyList<TriggerActionResult> results,
        DateTime? scheduledBoundaryUtc)
    {
        return new
        {
            trigger_id = trigger.Id,
            trigger_name = trigger.Name,
            boundary_utc = scheduledBoundaryUtc,
            actions = results.Select(r => new
            {
                kind = r.Kind,
                status = r.Status.ToString().ToLowerInvariant(),
                summary = r.ChangeSummary,
                failure = r.FailureReason,
            }),
        };
    }

    private async Task SafeRecordAsync(TriggerRunRecord record, CancellationToken ct)
    {
        try
        {
            await _repo.RecordRunAsync(record, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record trigger_run for trigger {TriggerId}.", record.TriggerId);
        }
    }

    /// Writes one <see cref="TriggerRunOutcome.SkippedLoop"/> audit row per
    /// active trigger of the given activator-kind so admins can see in
    /// run-history that a chain-cap suppressed a pass on this ticket. The
    /// load itself is the same call <see cref="EvaluateAsync"/> would make
    /// next; we deliberately do not run the Selective short-circuit here —
    /// the cap-hit signal matters more than precision about which triggers
    /// "would have matched".
    private async Task RecordChainCapSkipAsync(
        Guid ticketId,
        long? ticketEventId,
        TriggerActivatorKind activatorKind,
        CancellationToken ct)
    {
        IReadOnlyList<TriggerRow> triggers;
        try
        {
            triggers = await _repo.LoadActiveAsync(activatorKind, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to load active triggers while recording SkippedLoop on ticket {TicketId}.", ticketId);
            return;
        }
        foreach (var t in triggers)
        {
            await SafeRecordAsync(new TriggerRunRecord(
                t.Id, ticketId, ticketEventId,
                TriggerRunOutcome.SkippedLoop, null, null, null), ct);
        }
    }

    private async Task SafeAuditAsync(AuditEvent evt, CancellationToken ct)
    {
        try
        {
            await _audit.LogAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit event {EventType}.", evt.EventType);
        }
    }

    private async Task SafeNotifyAsync(Guid ticketId, CancellationToken ct)
    {
        try
        {
            await _listNotifier.NotifyUpdatedAsync(ticketId, ct);
        }
        catch (Exception ex)
        {
            // Realtime broadcast is best-effort — a failure here must not
            // mask the trigger's actual outcome. The mutation already
            // committed; clients will catch up on their next poll / F5.
            _logger.LogWarning(ex, "Trigger realtime fan-out failed for ticket {TicketId}.", ticketId);
        }
    }
}
