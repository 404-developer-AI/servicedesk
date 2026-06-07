using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class TitleReviewPreviewer : ITriggerActionPreviewer
{
    public string Kind => "title_review";

    public Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
        => Task.FromResult(TriggerActionPreviewResult.WouldApply(
            Kind,
            new { note = "Prompts the agent to review the ticket title on first open." }));
}
