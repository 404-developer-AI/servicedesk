using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed <see cref="IGroupTagNotifier"/>: pushes to each member's
/// <c>user:{id}</c> group on <see cref="UserNotificationHub"/>.
public sealed class SignalRGroupTagNotifier : IGroupTagNotifier
{
    private readonly IHubContext<UserNotificationHub> _hub;

    public SignalRGroupTagNotifier(IHubContext<UserNotificationHub> hub) => _hub = hub;

    public Task GroupTagReceivedAsync(IReadOnlyCollection<Guid> userIds, GroupTagPush payload, CancellationToken ct)
        => userIds.Count == 0 ? Task.CompletedTask
            : _hub.Clients.Groups(userIds.Select(u => $"user:{u}").ToList()).SendAsync("GroupTagReceived", payload, ct);

    public Task GroupTagsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
        => userIds.Count == 0 ? Task.CompletedTask
            : _hub.Clients.Groups(userIds.Select(u => $"user:{u}").ToList()).SendAsync("GroupTagsChanged", ct);
}
