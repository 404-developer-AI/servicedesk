using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetPriorityHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;

    public SetPriorityHandler(SystemFieldMutator mutator) => _mutator = mutator;

    public string Kind => "set_priority";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "priority_id", out var newPriorityId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'priority_id'.");

        var outcome = await _mutator.ChangeFieldAsync(
            ctx.TicketId,
            SystemFieldDescriptor.Priority,
            currentValue: ctx.Ticket.PriorityId,
            newValue: newPriorityId,
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
