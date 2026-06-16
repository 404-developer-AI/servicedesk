using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Activity;

/// SignalR hub backing the agent activity feed (v0.0.42). On connect the
/// hub looks up <c>users.activity_feed_enabled</c> for the caller; if
/// false, the connection is allowed but never joins the broadcast group,
/// so the dashboard tile and admin page silently receive no live pushes
/// — same behaviour as if the feature is disabled. Only callers with the
/// flag on land in <see cref="ViewersGroup"/> and receive
/// <c>ActivityEvent</c> events from
/// <see cref="SignalRActivityBroadcaster"/>.
///
/// RequireAgent (not RequireAdmin) because the viewing flag is per-user
/// — an admin can grant viewing to an Agent.
[Authorize(Policy = "RequireAgent")]
public sealed class ActivityFeedHub : Hub
{
    public const string ViewersGroup = "activity-viewers";

    // Connections enrolled in ViewersGroup, with the recipient's user id +
    // role. A SignalR group can't be enumerated, but the broadcaster needs
    // to fan out a per-recipient masked payload (a viewer never sees the
    // subject/number of a ticket in a queue they can't access), so we
    // track membership alongside the group. Key: connectionId → subscriber.
    private static readonly ConcurrentDictionary<string, Subscriber> Subscribers = new();

    /// A live activity-feed subscriber: their user id + role, captured on
    /// connect so the broadcaster can resolve queue access without a
    /// claims principal on hand.
    public readonly record struct Subscriber(Guid UserId, string Role);

    /// Snapshot of current subscribers (connectionId → recipient), used by
    /// <see cref="SignalRActivityBroadcaster"/> to mask per recipient.
    public static IReadOnlyList<KeyValuePair<string, Subscriber>> GetSubscribers()
        => Subscribers.ToArray();

    private readonly IUserService _users;

    public ActivityFeedHub(IUserService users)
    {
        _users = users;
    }

    public override async Task OnConnectedAsync()
    {
        var idClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(idClaim, out var userId))
        {
            var enabled = await _users.GetActivityFeedEnabledAsync(userId, Context.ConnectionAborted);
            if (enabled)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, ViewersGroup);
                var role = Context.User?.FindFirstValue(ClaimTypes.Role) ?? "Agent";
                Subscribers[Context.ConnectionId] = new Subscriber(userId, role);
            }
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Subscribers.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}
