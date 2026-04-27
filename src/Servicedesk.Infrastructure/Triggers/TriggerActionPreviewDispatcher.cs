using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Triggers;

public sealed class TriggerActionPreviewDispatcher : ITriggerActionPreviewDispatcher
{
    private readonly IReadOnlyDictionary<string, ITriggerActionPreviewer> _previewers;
    private readonly ILogger<TriggerActionPreviewDispatcher> _logger;

    public TriggerActionPreviewDispatcher(
        IEnumerable<ITriggerActionPreviewer> previewers,
        ILogger<TriggerActionPreviewDispatcher> logger)
    {
        var map = new Dictionary<string, ITriggerActionPreviewer>(StringComparer.Ordinal);
        foreach (var p in previewers) map[p.Kind] = p;
        _previewers = map;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TriggerActionPreviewResult>> PreviewAllAsync(
        JsonElement actions, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (actions.ValueKind != JsonValueKind.Array)
            return Array.Empty<TriggerActionPreviewResult>();

        var results = new List<TriggerActionPreviewResult>(actions.GetArrayLength());
        foreach (var action in actions.EnumerateArray())
        {
            var kind = action.ValueKind == JsonValueKind.Object
                && action.TryGetProperty("kind", out var kindEl)
                && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(kind))
            {
                results.Add(TriggerActionPreviewResult.Failed(string.Empty, "Action has no 'kind' field."));
                continue;
            }

            if (!_previewers.TryGetValue(kind, out var previewer))
            {
                _logger.LogDebug("Trigger dry-run: action kind '{Kind}' has no registered previewer.", kind);
                results.Add(TriggerActionPreviewResult.NoHandler(kind));
                continue;
            }

            try
            {
                var result = await previewer.PreviewAsync(action, ctx, ct);
                results.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trigger dry-run previewer '{Kind}' threw on ticket {TicketId}.", kind, ctx.TicketId);
                results.Add(TriggerActionPreviewResult.Failed(kind, ex.GetType().Name + ": " + ex.Message));
            }
        }
        return results;
    }
}
