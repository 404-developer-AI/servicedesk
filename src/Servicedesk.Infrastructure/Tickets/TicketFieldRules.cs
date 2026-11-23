using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Tickets;

/// Verdict of <see cref="ITicketFieldRules.CheckAsync"/>: <see cref="TicketMutationCheck.Ok"/>
/// or the first rule the update breaks (with the blocking checklists for
/// <see cref="TicketMutationCheck.ChecklistIncomplete"/>).
public sealed record TicketFieldRuleVerdict(TicketMutationCheck Check, IReadOnlyList<ChecklistBlocker>? Blockers = null)
{
    public static readonly TicketFieldRuleVerdict Ok = new(TicketMutationCheck.Ok);
    public bool IsOk => Check == TicketMutationCheck.Ok;
}

/// The field-update rules that hold for every writer — agent, bulk leg and
/// trigger alike (v0.1.65, ROADMAP §2 C3). Before, the trigger handlers kept
/// their own copies of the project pin, the status scope and the checklist
/// close block next to <see cref="TicketMutationService"/>'s.
///
/// Who may act (queue access on the current and the target queue) is not
/// here: that depends on an actor, and a trigger runs as the system. The
/// status gates are not here either — they are a confirmation dialog for an
/// agent, not a rule. Order: pending-till, project pin, status scope,
/// checklist block.
public interface ITicketFieldRules
{
    Task<TicketFieldRuleVerdict> CheckAsync(Ticket current, TicketFieldUpdate update, CancellationToken ct);
}

public sealed class TicketFieldRules : ITicketFieldRules
{
    private readonly IQueueStatusScope _statusScope;
    private readonly IChecklistCloseGuard _checklistGuard;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _clock;

    public TicketFieldRules(
        IQueueStatusScope statusScope, IChecklistCloseGuard checklistGuard, ISettingsService settings, TimeProvider clock)
    {
        _statusScope = statusScope;
        _checklistGuard = checklistGuard;
        _settings = settings;
        _clock = clock;
    }

    public async Task<TicketFieldRuleVerdict> CheckAsync(Ticket current, TicketFieldUpdate update, CancellationToken ct)
    {
        // v0.1.44 — a new pending-till must lie in the future (server clock).
        // Re-sending the value the ticket already holds is not a change and
        // passes, so a stale form never blocks an unrelated edit.
        if (update.PendingTillUtc.HasValue
            && !PendingTillRules.SameInstant(update.PendingTillUtc, current.PendingTillUtc)
            && PendingTillRules.IsInPast(update.PendingTillUtc, _clock.GetUtcNow().UtcDateTime))
            return new(TicketMutationCheck.PendingTillInPast);

        // v0.0.105 — project tickets are pinned to the configured project
        // queue: any move to a different queue is refused.
        if (update.QueueId is Guid target && target != current.QueueId && current.IsProject
            && await ProjectQueuePin.GetPinnedQueueIdAsync(_settings, ct) is Guid pin && target != pin)
            return new(TicketMutationCheck.ProjectQueueLocked);

        // v0.0.40 — an explicit status must sit in the (target) queue's
        // allowed list. Without an explicit status the writer's auto-flip
        // handles a queue-only move.
        if (update.StatusId.HasValue
            && !await _statusScope.IsStatusAllowedAsync(update.QueueId ?? current.QueueId, update.StatusId.Value, ct))
            return new(TicketMutationCheck.StatusNotInQueueScope);

        // v0.0.103 — checklist close block: a hard rule, no confirmation and
        // no override; only when the status actually changes.
        if (update.StatusId.HasValue && update.StatusId.Value != current.StatusId)
        {
            var blockers = await _checklistGuard.FindBlockersAsync(current.Id, update.StatusId.Value, ct);
            if (blockers.Count > 0) return new(TicketMutationCheck.ChecklistIncomplete, blockers);
        }

        return TicketFieldRuleVerdict.Ok;
    }
}
