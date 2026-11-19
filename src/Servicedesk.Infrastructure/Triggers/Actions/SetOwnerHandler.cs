using System.Text.Json;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetOwnerHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;
    private readonly IUserService _users;
    private readonly IQueueAccessService _queueAccess;
    private readonly ITicketRepository _tickets;

    public SetOwnerHandler(
        SystemFieldMutator mutator, IUserService users, IQueueAccessService queueAccess, ITicketRepository tickets)
    {
        _mutator = mutator;
        _users = users;
        _queueAccess = queueAccess;
        _tickets = tickets;
    }

    public string Kind => "set_owner";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        Guid? newUserId;
        Guid? currentOwner = ctx.Ticket.AssigneeUserId;
        if (FormFieldActionSource.HasFormField(actionJson))
        {
            var source = FormFieldActionSource.Resolve(actionJson, ctx, FormFieldType.Agent);
            if (source.Error is not null) return TriggerActionResult.Failed(Kind, source.Error);
            if (source.EmptyReason is not null) return TriggerActionResult.NoOp(Kind, new { reason = source.EmptyReason });

            // Fresh read: a Set queue action earlier in this trigger decides
            // which queue the new owner must be able to open.
            var ticket = await _tickets.GetCoreAsync(ctx.TicketId, ct);
            if (ticket is null) return TriggerActionResult.Failed(Kind, "Ticket vanished mid-update.");
            var check = await FormOwnerCheck.ReasonCannotOwnAsync(_users, _queueAccess, source.Field!.RefId!.Value, ticket.QueueId, ct);
            if (check is not null) return TriggerActionResult.Failed(Kind, check);
            newUserId = source.Field.RefId;
            currentOwner = ticket.AssigneeUserId;
        }
        // assignee_user_id is nullable — explicit `user_id: null` from
        // the editor means "clear the assignee", not a malformed action.
        else if (!ActionJson.TryReadGuidOrNull(actionJson, "user_id", out newUserId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string-or-null 'user_id'.");

        var outcome = await _mutator.ChangeFieldAsync(
            ctx.TicketId,
            SystemFieldDescriptor.Owner,
            currentValue: currentOwner,
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

/// v0.1.58 — an owner taken from a form field must still be an active agent
/// or admin who can open the ticket's queue: a trigger runs as the system, so
/// without this a form could hand a ticket to someone who cannot see it.
internal static class FormOwnerCheck
{
    public static async Task<string?> ReasonCannotOwnAsync(
        IUserService users, IQueueAccessService queueAccess, Guid userId, Guid queueId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null || !user.IsActive || user.RoleName is not ("Agent" or "Admin"))
            return "The agent picked on the form is no longer an active agent.";
        if (!await queueAccess.HasQueueAccessAsync(user.Id, user.RoleName, queueId, ct))
            return $"{user.Email} has no access to the ticket's queue; owner not changed.";
        return null;
    }
}
