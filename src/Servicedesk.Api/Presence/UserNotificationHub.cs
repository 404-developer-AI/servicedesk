using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

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
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
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
