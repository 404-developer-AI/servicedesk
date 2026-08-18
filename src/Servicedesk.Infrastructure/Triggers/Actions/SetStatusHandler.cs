using System.Text.Json;
using Servicedesk.Infrastructure.Checklists;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetStatusHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;
    private readonly IChecklistCloseGuard _checklistGuard;
    private readonly IChecklistCloseBlockReporter _blockReporter;
    private readonly ITriggerRepository _triggers;

    public SetStatusHandler(
        SystemFieldMutator mutator,
        IChecklistCloseGuard checklistGuard,
        IChecklistCloseBlockReporter blockReporter,
        ITriggerRepository triggers)
    {
        _mutator = mutator;
        _checklistGuard = checklistGuard;
        _blockReporter = blockReporter;
        _triggers = triggers;
    }

    public string Kind => "set_status";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "status_id", out var newStatusId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'status_id'.");

        // v0.0.103 — checklist close block. A trigger that resolves/closes a
        // ticket (e.g. "Closed without Invoice" on a CWI note) must not slip
        // past a blocking checklist the agent could not close past by hand.
        // The action is skipped (no-op with the reason), the rest of the
        // trigger still runs, and the agent who caused it is told why.
        if (newStatusId != ctx.Ticket.StatusId)
        {
            var blockers = await _checklistGuard.FindBlockersAsync(ctx.TicketId, newStatusId, ct);
            if (blockers.Count > 0)
            {
                var trigger = ctx.TriggerId == Guid.Empty ? null : await _triggers.GetByIdAsync(ctx.TriggerId, ct);
                await _blockReporter.ReportTriggerBlockedAsync(
                    ctx.Ticket, ctx.TriggeringEvent, ctx.TriggerId, trigger?.Name ?? "trigger",
                    newStatusId, blockers, ct);
                return TriggerActionResult.NoOp(Kind, new
                {
                    column = "status_id",
                    reason = "checklist_incomplete",
                    to = newStatusId,
                    checklists = blockers.Select(b => new { b.ChecklistId, b.Name, b.OpenRequired }),
                });
            }
        }

        var outcome = await _mutator.ChangeStatusAsync(
            ctx.TicketId,
            currentStatusId: ctx.Ticket.StatusId,
            newStatusId: newStatusId,
            triggerId: ctx.TriggerId,
            ct: ct);

        return outcome.Status switch
        {
            FieldChangeStatus.Applied => TriggerActionResult.Applied(Kind, new
            {
                column = outcome.Column,
                from = outcome.From,
                to = outcome.To,
                fromName = outcome.FromName,
                toName = outcome.ToName,
            }),
            FieldChangeStatus.NoOp => TriggerActionResult.NoOp(Kind, new { column = outcome.Column }),
            _ => TriggerActionResult.Failed(Kind, outcome.Reason ?? "Unknown failure."),
        };
    }
}
