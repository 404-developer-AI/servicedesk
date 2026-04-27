using System.Text.Json;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

internal sealed class SetOwnerPreviewer : ITriggerActionPreviewer
{
    private readonly IUserService _users;

    public SetOwnerPreviewer(IUserService users) => _users = users;

    public string Kind => "set_owner";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "user_id", out var newUserId))
            return TriggerActionPreviewResult.Failed(Kind, "Action is missing required string 'user_id'.");

        if (ctx.Ticket.AssigneeUserId.HasValue && ctx.Ticket.AssigneeUserId.Value == newUserId)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { column = "assignee_user_id", reason = "already_at_target" });

        var to = await _users.FindByIdAsync(newUserId, ct);
        if (to is null)
            return TriggerActionPreviewResult.Failed(Kind, $"Target user {newUserId} not found.");

        var from = ctx.Ticket.AssigneeUserId.HasValue
            ? await _users.FindByIdAsync(ctx.Ticket.AssigneeUserId.Value, ct)
            : null;

        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            column = "assignee_user_id",
            from = ctx.Ticket.AssigneeUserId,
            to = (Guid?)newUserId,
            fromName = from?.Email,
            toName = to.Email,
        });
    }
}
