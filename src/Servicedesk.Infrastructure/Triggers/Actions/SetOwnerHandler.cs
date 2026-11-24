using System.Text.Json;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetOwnerHandler : ITriggerActionHandler
{
    private readonly TriggerFieldWrite _write;
    private readonly IUserService _users;
    private readonly IQueueAccessService _queueAccess;
    private readonly ITicketRepository _tickets;

    public SetOwnerHandler(
        TriggerFieldWrite write, IUserService users, IQueueAccessService queueAccess, ITicketRepository tickets)
    {
        _write = write;
        _users = users;
        _queueAccess = queueAccess;
        _tickets = tickets;
    }

    public string Kind => "set_owner";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        Guid? newUserId;
        string notActiveReason;
        if (FormFieldActionSource.HasFormField(actionJson))
        {
            var source = FormFieldActionSource.Resolve(actionJson, ctx, FormFieldType.Agent);
            if (source.Error is not null) return TriggerActionResult.Failed(Kind, source.Error);
            if (source.EmptyReason is not null) return TriggerActionResult.NoOp(Kind, new { reason = source.EmptyReason });
            newUserId = source.Field!.RefId;
            notActiveReason = "The agent picked on the form is no longer an active agent.";
        }
        // assignee_user_id is nullable — explicit `user_id: null` from
        // the editor means "clear the assignee", not a malformed action.
        else if (!ActionJson.TryReadGuidOrNull(actionJson, "user_id", out newUserId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string-or-null 'user_id'.");
        else
            notActiveReason = "The trigger's owner is not an active agent or admin.";

        if (newUserId is not Guid id)
            return await _write.ApplyAsync(Kind, "assignee_user_id", new TicketFieldUpdate(ClearAssignee: true), ctx, ct);

        // Fresh read: a Set queue action earlier in this trigger decides
        // which queue the new owner must be able to open.
        var ticket = await _tickets.GetCoreAsync(ctx.TicketId, ct);
        if (ticket is null) return TriggerActionResult.Failed(Kind, "Ticket vanished mid-update.");
        var check = await TriggerOwnerCheck.ReasonCannotOwnAsync(_users, _queueAccess, id, ticket.QueueId, notActiveReason, ct);
        if (check is not null) return TriggerActionResult.Failed(Kind, check);

        return await _write.ApplyAsync(Kind, "assignee_user_id", new TicketFieldUpdate(AssigneeUserId: id), ctx, ct);
    }
}

/// An owner set by a trigger — fixed in the action or taken from a form
/// field — must be an active agent or admin who can open the ticket's queue:
/// a trigger runs as the system, so without this it could hand a ticket to
/// someone who cannot see it (form-field mode since v0.1.58, fixed-value mode
/// since 2026-11-24).
internal static class TriggerOwnerCheck
{
    public static async Task<string?> ReasonCannotOwnAsync(
        IUserService users, IQueueAccessService queueAccess, Guid userId, Guid queueId,
        string notActiveReason, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null || !user.IsActive || user.RoleName is not ("Agent" or "Admin"))
            return notActiveReason;
        if (!await queueAccess.HasQueueAccessAsync(user.Id, user.RoleName, queueId, ct))
            return $"{user.Email} has no access to the ticket's queue; owner not changed.";
        return null;
    }
}
