using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Auth.Admin;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="ISecurityAlertNotifier"/>.
/// Looks up the current set of active Admins from
/// <see cref="IUserAdminService"/> and pushes <c>SecurityAlertReceived</c>
/// to each admin's <c>user:{userId}</c>-group on
/// <see cref="UserNotificationHub"/>. The admin-list is re-read on each
/// fan-out call — admin changes (role toggle, deactivate, delete) take
/// effect on the next alert without any cache-bust.
public sealed class SignalRSecurityAlertNotifier : ISecurityAlertNotifier
{
    private readonly IHubContext<UserNotificationHub> _hub;
    private readonly IUserAdminService _userAdmin;

    public SignalRSecurityAlertNotifier(
        IHubContext<UserNotificationHub> hub,
        IUserAdminService userAdmin)
    {
        _hub = hub;
        _userAdmin = userAdmin;
    }

    public async Task NotifyAdminsAsync(SecurityAlertPush payload, CancellationToken ct)
    {
        var users = await _userAdmin.ListAllAsync(ct);
        foreach (var u in users)
        {
            if (!u.IsActive) continue;
            if (!string.Equals(u.Role, "Admin", StringComparison.Ordinal)) continue;

            await _hub.Clients.Group($"user:{u.Id}")
                .SendAsync("SecurityAlertReceived", payload, ct);
        }
    }
}
