using System.Text.Json;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetStatusPreviewer : ITriggerActionPreviewer
{
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IChecklistCloseGuard _checklistGuard;

    public SetStatusPreviewer(ITaxonomyRepository taxonomy, IChecklistCloseGuard checklistGuard)
    {
        _taxonomy = taxonomy;
        _checklistGuard = checklistGuard;
    }

    public string Kind => "set_status";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "status_id", out var newStatusId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string 'status_id'.");

        if (ctx.Ticket.StatusId == newStatusId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "status_id", reason = "already_at_target" });

        // v0.0.103 — mirror the live handler: a blocking checklist with
        // required open items makes the status change a no-op.
        var blockers = await _checklistGuard.FindBlockersAsync(ctx.TicketId, newStatusId, ct);
        if (blockers.Count > 0)
        {
            return TriggerActionPreviewResult.WouldNoOp(Kind, new
            {
                column = "status_id",
                reason = "checklist_incomplete",
                checklists = blockers.Select(b => new { b.ChecklistId, b.Name, b.OpenRequired }),
            });
        }

        var to = await _taxonomy.GetStatusAsync(newStatusId, ct);
        if (to is null)
            return TriggerActionPreviewResult.Failed(Kind, $"Target status {newStatusId} not found.");

        var from = await _taxonomy.GetStatusAsync(ctx.Ticket.StatusId, ct);

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            column = "status_id",
            from = (Guid?)ctx.Ticket.StatusId,
            to = (Guid?)newStatusId,
            fromName = from?.Name,
            toName = to.Name,
            fromCategory = from?.StateCategory,
            toCategory = to.StateCategory,
        });
    }
}
