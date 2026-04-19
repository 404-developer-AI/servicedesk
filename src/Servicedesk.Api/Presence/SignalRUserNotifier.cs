using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="IUserNotifier"/>. Pushes
/// `NotificationReceived` to the <c>user:{userId}</c> group maintained by
/// <see cref="UserNotificationHub"/>.
public sealed class SignalRUserNotifier : IUserNotifier
{
    private readonly IHubContext<UserNotificationHub> _hub;

    public SignalRUserNotifier(IHubContext<UserNotificationHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyMentionAsync(Guid userId, UserNotificationPush payload, CancellationToken ct)
        => _hub.Clients.Group($"user:{userId}")
            .SendAsync("NotificationReceived", payload, ct);
}
