using System.Text.Json;
using Servicedesk.Infrastructure.Forms;
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

        // The Pending-status check (both modes) is left to the real run: in a
        // dry run an earlier Set status action of the same trigger is not
        // applied.
        DateTime? target;
        if (FormFieldActionSource.HasFormField(actionJson))
        {
            var source = FormFieldActionSource.Resolve(actionJson, ctx, FormFieldType.DateTime);
            if (source.Error is not null) return TriggerActionPreviewResult.Failed(Kind, source.Error);
            if (source.EmptyReason is not null) return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = source.EmptyReason });
            target = source.Field!.DateTimeUtc;
        }
        else
        {
            var (resolved, error) = await SetPendingTillResolver.ResolveAsync(actionJson, ctx, _slaRepository, ct);
            if (error is not null) return TriggerActionPreviewResult.Failed(Kind, error);
            target = resolved;
        }

        if (PendingTillRules.IsInPast(target, DateTime.UtcNow))
            return TriggerActionPreviewResult.Failed(Kind, $"Target time {target!.Value:yyyy-MM-ddTHH:mm:ssZ} is in the past; pending-till not set.");

        var (nextTriggerId, nextError) = SetPendingTillResolver.ResolveNextTriggerId(actionJson);
        if (nextError is not null) return TriggerActionPreviewResult.Failed(Kind, nextError);

        return TriggerActionPreviewResult.WouldApply(Kind, new { pendingTillUtc = target, nextTriggerId });
    }
}
