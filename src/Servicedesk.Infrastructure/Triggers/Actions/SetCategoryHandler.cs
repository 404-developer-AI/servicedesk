using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Sets (or clears, with <c>category_id: null</c>) the ticket category.
/// The category marks the phase of a ticket independently of its status
/// (back-office intervention flow: "to plan" / "planned" / "to finish"), so
/// a status change never loses it. Same shared field write as every other
/// field action (<see cref="TriggerFieldWrite"/>): rules, UPDATE,
/// <c>CategoryChange</c> event, SLA recompute.
internal sealed class SetCategoryHandler : ITriggerActionHandler
{
    private readonly TriggerFieldWrite _write;

    public SetCategoryHandler(TriggerFieldWrite write) => _write = write;

    public string Kind => "set_category";

    public Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        // category_id is nullable — an explicit null clears the category.
        if (!ActionJson.TryReadGuidOrNull(actionJson, "category_id", out var newCategoryId))
            return Task.FromResult(TriggerActionResult.Failed(Kind, "Action is missing required string-or-null 'category_id'."));

        var update = newCategoryId is Guid id
            ? new TicketFieldUpdate(CategoryId: id)
            : new TicketFieldUpdate(ClearCategory: true);
        return _write.ApplyAsync(Kind, "category_id", update, ctx, ct);
    }
}
