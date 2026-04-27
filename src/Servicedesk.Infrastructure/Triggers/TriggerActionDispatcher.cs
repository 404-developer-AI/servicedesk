using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Triggers;

public sealed class TriggerActionDispatcher : ITriggerActionDispatcher
{
    private readonly IReadOnlyDictionary<string, ITriggerActionHandler> _handlers;
    private readonly ILogger<TriggerActionDispatcher> _logger;

    public TriggerActionDispatcher(
        IEnumerable<ITriggerActionHandler> handlers,
        ILogger<TriggerActionDispatcher> logger)
    {
        // Last-registration-wins on duplicate kinds. Block 3 ships one
        // handler per kind so this only matters if a future block adds
        // an override (and we want it to take effect).
        var map = new Dictionary<string, ITriggerActionHandler>(StringComparer.Ordinal);
        foreach (var h in handlers) map[h.Kind] = h;
        _handlers = map;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TriggerActionResult>> DispatchAllAsync(
        JsonElement actions, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (actions.ValueKind != JsonValueKind.Array)
            return Array.Empty<TriggerActionResult>();

        var results = new List<TriggerActionResult>(actions.GetArrayLength());
        foreach (var action in actions.EnumerateArray())
        {
            var kind = action.ValueKind == JsonValueKind.Object
                && action.TryGetProperty("kind", out var kindEl)
                && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(kind))
            {
                results.Add(TriggerActionResult.Failed(string.Empty, "Action has no 'kind' field."));
                continue;
            }

            if (!_handlers.TryGetValue(kind, out var handler))
            {
                _logger.LogWarning("Trigger action kind '{Kind}' has no registered handler — skipping.", kind);
                results.Add(TriggerActionResult.NoHandler(kind));
                continue;
            }

            try
            {
                var result = await handler.ApplyAsync(action, ctx, ct);
                results.Add(result);
            }
            catch (Exception ex)
            {
                // Per TRIGGERS.md §5, a handler failure is logged as
                // outcome=failed but does not roll back the original
                // ticket mutation. Other actions in the same trigger
                // continue — they may be independent (e.g. add_tag
                // shouldn't be skipped because send_mail failed).
                _logger.LogError(ex, "Trigger action '{Kind}' failed on ticket {TicketId}.", kind, ctx.TicketId);
                results.Add(TriggerActionResult.Failed(kind, ex.GetType().Name + ": " + ex.Message));
            }
        }
        return results;
    }
}
