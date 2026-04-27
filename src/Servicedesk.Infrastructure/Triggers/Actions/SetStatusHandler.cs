using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetStatusHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;

    public SetStatusHandler(SystemFieldMutator mutator) => _mutator = mutator;

    public string Kind => "set_status";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "status_id", out var newStatusId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'status_id'.");

        var outcome = await _mutator.ChangeStatusAsync(
            ctx.TicketId,
            currentStatusId: ctx.Ticket.StatusId,
            newStatusId: newStatusId,
            triggerId: ctx.TriggerId,
            ct: ct);

        return outcome.Status switch
        {
            FieldChangeStatus.Applied => TriggerActionResult.Applied(Kind, new
            {
                column = outcome.Column,
                from = outcome.From,
                to = outcome.To,
                fromName = outcome.FromName,
                toName = outcome.ToName,
            }),
            FieldChangeStatus.NoOp => TriggerActionResult.NoOp(Kind, new { column = outcome.Column }),
            _ => TriggerActionResult.Failed(Kind, outcome.Reason ?? "Unknown failure."),
        };
    }
}
