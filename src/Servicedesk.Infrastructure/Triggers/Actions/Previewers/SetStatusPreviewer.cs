using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetStatusPreviewer : ITriggerActionPreviewer
{
    private readonly ITaxonomyRepository _taxonomy;

    public SetStatusPreviewer(ITaxonomyRepository taxonomy) => _taxonomy = taxonomy;

    public string Kind => "set_status";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "status_id", out var newStatusId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string 'status_id'.");

        if (ctx.Ticket.StatusId == newStatusId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "status_id", reason = "already_at_target" });

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
