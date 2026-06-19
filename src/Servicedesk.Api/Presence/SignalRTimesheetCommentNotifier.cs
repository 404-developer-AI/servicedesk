using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed <see cref="ITimesheetCommentNotifier"/>. Pushes
/// `TimesheetCommentReceived` to the <c>user:{userId}</c> group maintained by
/// <see cref="UserNotificationHub"/> (the same per-user hub the mention
/// raamwerk uses), so the linked recipient's open session refreshes its
/// Comments inbox and red dots without a poll.
public sealed class SignalRTimesheetCommentNotifier : ITimesheetCommentNotifier
{
    private readonly IHubContext<UserNotificationHub> _hub;

    public SignalRTimesheetCommentNotifier(IHubContext<UserNotificationHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyCommentAsync(Guid userId, TimesheetCommentPush payload, CancellationToken ct)
        => _hub.Clients.Group($"user:{userId}")
            .SendAsync("TimesheetCommentReceived", payload, ct);
}
