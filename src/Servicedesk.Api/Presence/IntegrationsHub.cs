using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;

namespace Servicedesk.Api.Presence;

/// Admin-only SignalR hub used by the v0.0.25 integration framework. Pure
/// server-push surface — no client-invoked methods. Connected admins
/// receive <c>IntegrationStatusUpdated(integration, state)</c> whenever an
/// integration's resolved state changes (healthcheck-tick transitions,
/// connect/disconnect, secret-CRUD). The SPA uses each push as a "stale,
/// refetch" signal and re-runs the existing /status query for the full
/// payload, so this channel never carries authorized-subject / email and
/// stays cheap on the wire.
///
/// <para>
/// Authorize policy is RequireAdmin so an Agent who happens to know the
/// hub URL can't subscribe to integration health — admins are the only
/// audience for these notifications.
/// </para>
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public sealed class IntegrationsHub : Hub
{
    /// Group all admin connections subscribe to. Single group is fine —
    /// every admin gets every integration status; there is no per-user
    /// partition unlike <see cref="UserNotificationHub"/>.
    public const string IntegrationsGroup = "integrations";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, IntegrationsGroup);
        await base.OnConnectedAsync();
    }
}
