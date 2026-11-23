using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// The trigger side of the one field write path (v0.1.65, ROADMAP §2 C3):
/// the shared <see cref="ITicketFieldRules"/> first, then the shared
/// <see cref="TicketFieldWriter"/> with the trigger as origin, then the SLA
/// recalc. Every field action (queue, status, priority, category, owner)
/// goes through here, so a trigger can neither skip a rule an agent is held
/// to nor write the row differently (status auto-flip, Resolved/Closed
/// stamps, Pending-till clear).
///
/// A broken rule is a logged no-op with its reason — the rest of the trigger
/// still runs. A checklist close block also tells the agent who caused the
/// pass why the status did not change.
internal sealed class TriggerFieldWrite
{
    private static readonly TicketFieldUpdate NoChange = new();

    private readonly ITicketFieldRules _rules;
    private readonly TicketFieldWriter _writer;
    private readonly ISlaEngine _sla;
    private readonly IChecklistCloseBlockReporter _blockReporter;
    private readonly ITriggerRepository _triggers;
    private readonly ITicketRepository _tickets;

    public TriggerFieldWrite(
        ITicketFieldRules rules, TicketFieldWriter writer, ISlaEngine sla,
        IChecklistCloseBlockReporter blockReporter, ITriggerRepository triggers, ITicketRepository tickets)
    {
        _tickets = tickets;
        _rules = rules;
        _writer = writer;
        _sla = sla;
        _blockReporter = blockReporter;
        _triggers = triggers;
    }

    /// Applies <paramref name="update"/> and reports the change of
    /// <paramref name="column"/> (the action's own field) in the result. A
    /// queue move that flipped the status into the new queue's scope adds
    /// <c>statusFlippedTo</c>.
    public async Task<TriggerActionResult> ApplyAsync(
        string kind, string column, TicketFieldUpdate update, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        // Fresh read: an earlier action of the same trigger may already have
        // moved the queue or status, so the pass's snapshot can be stale.
        var current = await _tickets.GetCoreAsync(ctx.TicketId, ct);
        if (current is null) return TriggerActionResult.Failed(kind, "Ticket vanished mid-update.");

        // Re-setting the current status is no change: judging it against the
        // scope or a checklist would only produce a misleading reason.
        if (update.StatusId == current.StatusId) update = update with { StatusId = null };
        if (update == NoChange) return TriggerActionResult.NoOp(kind, new { column });

        var verdict = await _rules.CheckAsync(current, update, ct);
        if (!verdict.IsOk)
            return await RefusedAsync(kind, column, update, verdict, current, ctx, ct);

        var result = await _writer.ApplyAsync(ctx.TicketId, update, new TicketWriteOrigin.Trigger(ctx.TriggerId), ct);
        switch (result.Status)
        {
            case TicketFieldWriteStatus.NoOp:
                return TriggerActionResult.NoOp(kind, new { column });
            case TicketFieldWriteStatus.TicketGone:
                return TriggerActionResult.Failed(kind, "Ticket vanished mid-update.");
            case TicketFieldWriteStatus.TargetNotFound:
                return TriggerActionResult.Failed(kind, $"The {result.MissingTarget} this action points to no longer exists.");
        }

        // Any field change re-runs the SLA engine (same as the agent path):
        // Resolved/Closed marks deadlines met, queue/priority pick up the
        // new schedule.
        await _sla.OnTicketFieldsChangedAsync(ctx.TicketId, ct);

        var change = result.Change(column);
        if (change is null) return TriggerActionResult.NoOp(kind, new { column });
        var summary = new Dictionary<string, object?>
        {
            ["column"] = column,
            ["from"] = change.From,
            ["to"] = change.To,
            ["fromName"] = change.FromName,
            ["toName"] = change.ToName,
        };
        if (column != "status_id" && result.Change("status_id") is { } flip)
            summary["statusFlippedTo"] = flip.To;
        return TriggerActionResult.Applied(kind, summary);
    }

    private async Task<TriggerActionResult> RefusedAsync(
        string kind, string column, TicketFieldUpdate update, TicketFieldRuleVerdict verdict,
        Servicedesk.Domain.Tickets.Ticket current, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        switch (verdict.Check)
        {
            case TicketMutationCheck.ProjectQueueLocked:
                return TriggerActionResult.NoOp(kind, new
                {
                    column,
                    reason = "Suppressed: project tickets are pinned to the project queue.",
                });
            case TicketMutationCheck.StatusNotInQueueScope:
                return TriggerActionResult.NoOp(kind, new { column, reason = "status_not_in_queue_scope", to = update.StatusId });
            case TicketMutationCheck.ChecklistIncomplete:
                // v0.0.103 — e.g. "Closed without Invoice" on a CWI note must
                // not slip past a checklist the agent could not close past.
                var blockers = verdict.Blockers!;
                var trigger = ctx.TriggerId == Guid.Empty ? null : await _triggers.GetByIdAsync(ctx.TriggerId, ct);
                await _blockReporter.ReportTriggerBlockedAsync(
                    current, ctx.TriggeringEvent, ctx.TriggerId, trigger?.Name ?? "trigger",
                    update.StatusId!.Value, blockers, ct);
                return TriggerActionResult.NoOp(kind, new
                {
                    column,
                    reason = "checklist_incomplete",
                    to = update.StatusId,
                    checklists = blockers.Select(b => new { b.ChecklistId, b.Name, b.OpenRequired }),
                });
            default:
                return TriggerActionResult.NoOp(kind, new { column, reason = verdict.Check.ToString() });
        }
    }
}
