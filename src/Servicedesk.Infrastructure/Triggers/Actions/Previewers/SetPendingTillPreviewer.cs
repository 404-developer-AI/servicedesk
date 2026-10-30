using System.Text.Json;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers.Actions;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetPendingTillPreviewer : ITriggerActionPreviewer
{
    private readonly ISlaRepository _slaRepository;

    public SetPendingTillPreviewer(ISlaRepository slaRepository)
    {
        _slaRepository = slaRepository;
    }

    public string Kind => "set_pending_till";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        // Mirrors SetPendingTillHandler input shapes — keep failures
        // here aligned with the real handler so the preview surfaces
        // exactly what a real run would reject.
        if (actionJson.TryGetProperty("clear", out var clearEl) && clearEl.ValueKind == JsonValueKind.True)
        {
            return TriggerActionPreviewResult.WouldApply(Kind, new { cleared = true });
        }

        var (target, error) = await SetPendingTillResolver.ResolveAsync(actionJson, ctx, _slaRepository, ct);
        if (error is not null) return TriggerActionPreviewResult.Failed(Kind, error);

        if (PendingTillRules.IsInPast(target, DateTime.UtcNow))
            return TriggerActionPreviewResult.Failed(Kind, $"Target time {target!.Value:yyyy-MM-ddTHH:mm:ssZ} is in the past; pending-till not set.");

        var (nextTriggerId, nextError) = SetPendingTillResolver.ResolveNextTriggerId(actionJson);
        if (nextError is not null) return TriggerActionPreviewResult.Failed(Kind, nextError);

        return TriggerActionPreviewResult.WouldApply(Kind, new { pendingTillUtc = target, nextTriggerId });
    }
}
