using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetCategoryPreviewer : ITriggerActionPreviewer
{
    private readonly ITaxonomyRepository _taxonomy;

    public SetCategoryPreviewer(ITaxonomyRepository taxonomy) => _taxonomy = taxonomy;

    public string Kind => "set_category";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuidOrNull(actionJson, "category_id", out var newCategoryId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string-or-null 'category_id'.");

        if (ctx.Ticket.CategoryId == newCategoryId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "category_id", reason = "already_at_target" });

        string? toName = null;
        if (newCategoryId.HasValue)
        {
            var to = await _taxonomy.GetCategoryAsync(newCategoryId.Value, ct);
            if (to is null)
                return TriggerActionPreviewResult.Failed(Kind, $"Target category {newCategoryId} not found.");
            toName = to.Name;
        }

        var from = ctx.Ticket.CategoryId.HasValue
            ? await _taxonomy.GetCategoryAsync(ctx.Ticket.CategoryId.Value, ct)
            : null;

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            column = "category_id",
            from = ctx.Ticket.CategoryId,
            to = newCategoryId,
            fromName = from?.Name,
            toName,
        });
    }
}
