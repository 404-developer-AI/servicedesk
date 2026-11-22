using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Sets (or clears, with <c>category_id: null</c>) the ticket category.
/// The category marks the phase of a ticket independently of its status
/// (back-office intervention flow: "to plan" / "planned" / "to finish"), so
/// a status change never loses it. Same shared mutator path as
/// <c>set_priority</c>: lookup, UPDATE, <c>CategoryChange</c> event, SLA
/// recompute.
internal sealed class SetCategoryHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;

    public SetCategoryHandler(SystemFieldMutator mutator) => _mutator = mutator;

    public string Kind => "set_category";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuidOrNull(actionJson, "category_id", out var newCategoryId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string-or-null 'category_id'.");

        var outcome = await _mutator.ChangeFieldAsync(
            ctx.TicketId,
            SystemFieldDescriptor.Category,
            currentValue: ctx.Ticket.CategoryId,
            newValue: newCategoryId,
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
