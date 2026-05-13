using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class RepostAsPublicReplyPreviewer : ITriggerActionPreviewer
{
    public string Kind => "repost_as_public_reply";

    public Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        var src = ctx.TriggeringEvent;
        if (src is null)
            return Task.FromResult(TriggerActionPreviewResult.Failed(Kind, "Requires a triggering article to repost."));
        if (!src.IsInternal)
            return Task.FromResult(TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = "triggering article is already public" }));
        if (string.IsNullOrWhiteSpace(src.BodyHtml) && string.IsNullOrWhiteSpace(src.BodyText))
            return Task.FromResult(TriggerActionPreviewResult.Failed(Kind, "Triggering article has no body to repost."));

        return Task.FromResult(TriggerActionPreviewResult.WouldApply(Kind, new
        {
            isInternal = false,
            fromEventId = src.Id,
            bodyHtml = src.BodyHtml,
            bodyText = src.BodyText,
        }));
    }
}
