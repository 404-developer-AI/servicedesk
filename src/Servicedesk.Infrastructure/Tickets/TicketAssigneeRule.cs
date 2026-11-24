using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Infrastructure.Tickets;

/// The one rule every assignee writer shares: a ticket can only be assigned
/// to an active Agent or Admin. Before, the side-panel PATCH, bulk, ticket
/// create and the fixed-value trigger <c>set_owner</c> only checked that the
/// user row existed, so a hand-crafted call could assign a customer account
/// or a deactivated user. An unknown id answers the same as a non-staff one.
///
/// Writers: PATCH + bulk + trigger <c>set_owner</c> (via
/// <see cref="TicketFieldRules"/>), ticket create (endpoint). Queue access of
/// the assignee is deliberately not part of it — only trigger
/// <c>set_owner</c> (both modes) checks that (<c>TriggerOwnerCheck</c>), since
/// a trigger runs as the system.
public static class TicketAssigneeRule
{
    public const string Code = "assignee_not_assignable";
    public const string Message = "Tickets can only be assigned to an active agent or admin.";

    public static async Task<bool> IsAssignableAsync(IUserService users, Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        return user is { IsActive: true, RoleName: "Agent" or "Admin" };
    }
}
