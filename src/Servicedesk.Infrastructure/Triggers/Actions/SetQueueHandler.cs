using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetQueueHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;

    public SetQueueHandler(SystemFieldMutator mutator) => _mutator = mutator;

    public string Kind => "set_queue";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "queue_id", out var newQueueId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'queue_id'.");

        var outcome = await _mutator.ChangeFieldAsync(
            ctx.TicketId,
            SystemFieldDescriptor.Queue,
            currentValue: ctx.Ticket.QueueId,
            newValue: newQueueId,
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
