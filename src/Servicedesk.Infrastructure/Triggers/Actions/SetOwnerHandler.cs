using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetOwnerHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;

    public SetOwnerHandler(SystemFieldMutator mutator) => _mutator = mutator;

    public string Kind => "set_owner";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "user_id", out var newUserId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'user_id'.");

        var outcome = await _mutator.ChangeFieldAsync(
            ctx.TicketId,
            columnName: "assignee_user_id",
            lookupTable: "users",
            lookupColumn: "email",
            eventType: "AssignmentChange",
            currentValue: ctx.Ticket.AssigneeUserId,
            newValue: newUserId,
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
