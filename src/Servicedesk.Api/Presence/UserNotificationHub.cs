using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Presence;

/// Per-user SignalR hub used by the notification raamwerk (v0.0.12 stap 4).
/// On connect, the hub reads the user-id from the caller's session claims
/// and auto-joins them to the <c>user:{id}</c>-group, so
/// <see cref="Infrastructure.Realtime.IUserNotifier"/> publishers can push
/// `NotificationReceived` messages to exactly that user without having to
/// track connection-ids themselves.
///
/// <para>
/// There are no client-invoked methods in this release — the hub is a pure
/// server-push surface. Authentication is enforced with RequireAgent so a
/// customer (future portal) can't silently subscribe to an agent's
/// mention-feed by guessing the hub URL.
/// </para>
/// </summary>
[Authorize(Policy = "RequireAgent")]
public sealed class UserNotificationHub : Hub
{
    private readonly IUserService _users;

    public UserNotificationHub(IUserService users) => _users = users;

    public override async Task OnConnectedAsync()
    {
        if (ActorContext.TryGetUserId(Context.User) is { } userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
            // v0.1.54 — Back office forms holders get the payload-less
            // work-list refresh ping. The flag is read at connect time; the
            // ping carries no data, so a later revoke leaks nothing.
            if (await _users.GetBackofficeFormsEnabledAsync(userId))
                await Groups.AddToGroupAsync(Context.ConnectionId, SignalRBackofficeFormNotifier.Group);
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR cleans up per-connection group membership automatically on
        // disconnect; we only kept a reference for documentation purposes.
        return base.OnDisconnectedAsync(exception);
    }
}
