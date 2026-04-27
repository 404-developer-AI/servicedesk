using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetQueuePreviewer : ITriggerActionPreviewer
{
    private readonly ITaxonomyRepository _taxonomy;

    public SetQueuePreviewer(ITaxonomyRepository taxonomy) => _taxonomy = taxonomy;

    public string Kind => "set_queue";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "queue_id", out var newQueueId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string 'queue_id'.");

        if (ctx.Ticket.QueueId == newQueueId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "queue_id", reason = "already_at_target" });

        var to = await _taxonomy.GetQueueAsync(newQueueId, ct);
        if (to is null)
            return TriggerActionPreviewResult.Failed(Kind, $"Target queue {newQueueId} not found.");

        var from = await _taxonomy.GetQueueAsync(ctx.Ticket.QueueId, ct);

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            column = "queue_id",
            from = (Guid?)ctx.Ticket.QueueId,
            to = (Guid?)newQueueId,
            fromName = from?.Name,
            toName = to.Name,
        });
    }
}
