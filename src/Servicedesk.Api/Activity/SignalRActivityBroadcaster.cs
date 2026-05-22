using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Activity;

namespace Servicedesk.Api.Activity;

/// Pushes <see cref="ActivityFeedEntry"/> rows to every client currently
/// enrolled in <see cref="ActivityFeedHub.ViewersGroup"/>. Called by the
/// <c>ActivityListenerWorker</c> (Infrastructure) once it has enriched
/// the row that triggered the Postgres NOTIFY.
public sealed class SignalRActivityBroadcaster : IActivityBroadcaster
{
    private readonly IHubContext<ActivityFeedHub> _hub;

    public SignalRActivityBroadcaster(IHubContext<ActivityFeedHub> hub)
    {
        _hub = hub;
    }

    public Task BroadcastAsync(ActivityFeedEntry entry, CancellationToken cancellationToken = default)
    {
        return _hub.Clients
            .Group(ActivityFeedHub.ViewersGroup)
            .SendAsync("ActivityEvent", entry, cancellationToken);
    }
}
