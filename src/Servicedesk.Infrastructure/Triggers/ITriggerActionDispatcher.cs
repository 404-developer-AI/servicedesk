using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers;

public interface ITriggerActionDispatcher
{
    /// Iterates the actions JSONB array in order, dispatching each item
    /// to the handler registered for its <c>kind</c>. Returns one
    /// <see cref="TriggerActionResult"/> per action element so the
    /// evaluator can persist the full diff in
    /// <c>trigger_runs.applied_changes</c>.
    Task<IReadOnlyList<TriggerActionResult>> DispatchAllAsync(
        JsonElement actions,
        TriggerEvaluationContext ctx,
        CancellationToken ct);
}
