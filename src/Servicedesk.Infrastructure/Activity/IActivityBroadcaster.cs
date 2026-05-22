namespace Servicedesk.Infrastructure.Activity;

/// Implemented by the API layer (SignalR) and consumed by the
/// <c>ActivityListenerWorker</c> in Infrastructure. Lives here so the
/// listener does not need a hard reference on the API project.
public interface IActivityBroadcaster
{
    Task BroadcastAsync(ActivityFeedEntry entry, CancellationToken cancellationToken = default);
}
