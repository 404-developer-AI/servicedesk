using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Placeholder handler for the <c>title_review</c> first-open gate action.
/// The interactive part (showing the dialog, capturing the edited subject)
/// happens in the agent UI, and the subject is applied by the open-gate
/// confirmation endpoint before it dispatches the trigger's remaining
/// actions. This handler exists only so dispatching the trigger's full
/// action list — and the admin dry-run previewer — doesn't report a
/// NoHandler gap for the gate action itself. It never mutates anything.
internal sealed class TitleReviewHandler : ITriggerActionHandler
{
    public string Kind => "title_review";

    public Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
        => Task.FromResult(TriggerActionResult.NoOp(Kind, new { note = "Title review is applied by the open-gate endpoint." }));
}
