using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Activity;

namespace Servicedesk.Api.Activity;

/// Pushes <see cref="ActivityFeedEntry"/> rows to every client currently
/// enrolled in <see cref="ActivityFeedHub.ViewersGroup"/>. Called by the
/// <c>ActivityListenerWorker</c> (Infrastructure) once it has enriched
/// the row that triggered the Postgres NOTIFY.
///
/// Ticket rows are masked per recipient: a viewer without access to the
/// ticket's queue receives a "restricted" placeholder rather than its
/// subject/number — the same queue-level authorization the list reads and
/// the ticket-detail endpoint enforce. We therefore fan out per recipient
/// (via the hub's subscriber registry) rather than broadcasting one
/// shared payload to the group.
public sealed class SignalRActivityBroadcaster : IActivityBroadcaster
{
    private readonly IHubContext<ActivityFeedHub> _hub;
    private readonly IQueueAccessService _queueAccess;

    public SignalRActivityBroadcaster(IHubContext<ActivityFeedHub> hub, IQueueAccessService queueAccess)
    {
        _hub = hub;
        _queueAccess = queueAccess;
    }

    public async Task BroadcastAsync(ActivityFeedEntry entry, CancellationToken cancellationToken = default)
    {
        var subscribers = ActivityFeedHub.GetSubscribers();
        if (subscribers.Count == 0) return;

        // Rows with no ticket queue (non-ticket events, or a deleted
        // ticket) carry nothing to mask — one shared send to everyone.
        if (entry.TicketQueueId is null)
        {
            var all = subscribers.Select(s => s.Key).ToArray();
            await _hub.Clients.Clients(all).SendAsync("ActivityEvent", entry, cancellationToken);
            return;
        }

        // Group connections by recipient so queue access is resolved once
        // per user (cached anyway), then send a masked copy per recipient.
        foreach (var byUser in subscribers.GroupBy(s => s.Value.UserId))
        {
            var role = byUser.First().Value.Role;
            var scope = await _queueAccess.GetScopeAsync(byUser.Key, role, cancellationToken);
            var masked = ActivityFeedMasking.ForViewer(entry, scope);
            var connectionIds = byUser.Select(s => s.Key).ToArray();
            await _hub.Clients.Clients(connectionIds).SendAsync("ActivityEvent", masked, cancellationToken);
        }
    }
}
