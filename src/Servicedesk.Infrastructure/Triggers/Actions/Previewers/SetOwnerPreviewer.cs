using System.Text.Json;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Forms;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetOwnerPreviewer : ITriggerActionPreviewer
{
    private readonly IUserService _users;

    public SetOwnerPreviewer(IUserService users) => _users = users;

    public string Kind => "set_owner";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        Guid? newUserId;
        if (FormFieldActionSource.HasFormField(actionJson))
        {
            var source = FormFieldActionSource.Resolve(actionJson, ctx, FormFieldType.Agent);
            if (source.Error is not null) return TriggerActionPreviewResult.Failed(Kind, source.Error);
            if (source.EmptyReason is not null) return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = source.EmptyReason });
            // Queue access is checked against the queue the ticket ends up
            // in, which a dry run cannot know (an earlier Set queue action is
            // not applied) — so only the "still an active agent" part here.
            var picked = await _users.FindByIdAsync(source.Field!.RefId!.Value, ct);
            if (picked is null || !picked.IsActive || picked.RoleName is not ("Agent" or "Admin"))
                return TriggerActionPreviewResult.Failed(Kind, "The agent picked on the form is no longer an active agent.");
            newUserId = picked.Id;
        }
        else if (!ActionJson.TryReadGuidOrNull(actionJson, "user_id", out newUserId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string-or-null 'user_id'.");

        if (ctx.Ticket.AssigneeUserId == newUserId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "assignee_user_id", reason = "already_at_target" });

        var to = newUserId.HasValue
            ? await _users.FindByIdAsync(newUserId.Value, ct)
            : null;
        if (newUserId.HasValue && to is null)
            return TriggerActionPreviewResult.Failed(Kind, $"Target user {newUserId.Value} not found.");

        var from = ctx.Ticket.AssigneeUserId.HasValue
            ? await _users.FindByIdAsync(ctx.Ticket.AssigneeUserId.Value, ct)
            : null;

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            column = "assignee_user_id",
            from = ctx.Ticket.AssigneeUserId,
            to = newUserId,
            fromName = from?.Email,
            toName = to?.Email,
        });
    }
}
