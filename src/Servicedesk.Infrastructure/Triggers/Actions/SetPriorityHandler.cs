using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetPriorityHandler : ITriggerActionHandler
{
    private readonly TriggerFieldWrite _write;

    public SetPriorityHandler(TriggerFieldWrite write) => _write = write;

    public string Kind => "set_priority";

    public Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "priority_id", out var newPriorityId))
            return Task.FromResult(TriggerActionResult.Failed(Kind, "Action is missing required string 'priority_id'."));

        return _write.ApplyAsync(Kind, "priority_id", new TicketFieldUpdate(PriorityId: newPriorityId), ctx, ct);
    }
}
