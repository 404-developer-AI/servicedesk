using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers;

public interface ITriggerActionPreviewDispatcher
{
    /// Walks the actions JSONB array in declaration order and routes each
    /// element to the previewer registered for its <c>kind</c>. Returns
    /// one <see cref="TriggerActionPreviewResult"/> per action so the
    /// admin test-runner UI can render a per-action diff list.
    Task<IReadOnlyList<TriggerActionPreviewResult>> PreviewAllAsync(
        JsonElement actions,
        TriggerEvaluationContext ctx,
        CancellationToken ct);
}
