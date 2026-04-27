using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetPriorityPreviewer : ITriggerActionPreviewer
{
    private readonly ITaxonomyRepository _taxonomy;

    public SetPriorityPreviewer(ITaxonomyRepository taxonomy) => _taxonomy = taxonomy;

    public string Kind => "set_priority";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "priority_id", out var newPriorityId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string 'priority_id'.");

        if (ctx.Ticket.PriorityId == newPriorityId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "priority_id", reason = "already_at_target" });

        var to = await _taxonomy.GetPriorityAsync(newPriorityId, ct);
        if (to is null)
            return TriggerActionPreviewResult.Failed(Kind, $"Target priority {newPriorityId} not found.");

        var from = await _taxonomy.GetPriorityAsync(ctx.Ticket.PriorityId, ct);

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            column = "priority_id",
            from = (Guid?)ctx.Ticket.PriorityId,
            to = (Guid?)newPriorityId,
            fromName = from?.Name,
            toName = to.Name,
        });
    }
}
