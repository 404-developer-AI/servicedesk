using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Sets the ticket status through the shared field write: the status must be
/// allowed in the ticket's queue and a blocking checklist refuses a
/// resolve/close exactly as it does for an agent (logged no-op, the agent who
/// caused the pass is told why). The write itself stamps Resolved/Closed and
/// clears a Pending-till on any non-Pending target.
internal sealed class SetStatusHandler : ITriggerActionHandler
{
    private readonly TriggerFieldWrite _write;

    public SetStatusHandler(TriggerFieldWrite write) => _write = write;

    public string Kind => "set_status";

    public Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "status_id", out var newStatusId))
            return Task.FromResult(TriggerActionResult.Failed(Kind, "Action is missing required string 'status_id'."));

        return _write.ApplyAsync(Kind, "status_id", new TicketFieldUpdate(StatusId: newStatusId), ctx, ct);
    }
}
