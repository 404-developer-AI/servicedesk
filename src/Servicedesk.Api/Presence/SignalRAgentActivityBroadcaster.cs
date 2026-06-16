using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Dashboard;

namespace Servicedesk.Api.Presence;

/// Pushes per-user <see cref="AgentActivity"/> records to the
/// "agent-activity-admins" SignalR group. Used by the presence hub on
/// every viewing/recent change and by the Telavox polling worker on
/// every call-state edge so the dashboard tile receives a single
/// uniform stream of updates regardless of source.
///
/// The record is built once with full ticket data, then masked per
/// recipient: a viewer without access to a ticket's queue receives a
/// restricted placeholder instead of its subject/number — the same
/// queue-level authorization the ticket-detail endpoint enforces. We
/// therefore fan out per recipient (via the hub's subscriber registry)
/// rather than broadcasting one shared payload to the group.
public sealed class SignalRAgentActivityBroadcaster : IAgentActivityBroadcaster
{
    public const string AdminsGroup = "agent-activity-admins";

    private readonly IHubContext<TicketPresenceHub> _hub;
    private readonly IAgentActivityService _service;
    private readonly IQueueAccessService _queueAccess;

    public SignalRAgentActivityBroadcaster(
        IHubContext<TicketPresenceHub> hub,
        IAgentActivityService service,
        IQueueAccessService queueAccess)
    {
        _hub = hub;
        _service = service;
        _queueAccess = queueAccess;
    }

    public async Task BroadcastForUserAsync(Guid userId, CancellationToken ct)
    {
        var subscribers = TicketPresenceHub.GetAgentActivitySubscribers();
        if (subscribers.Count == 0) return;

        var presence = TicketPresenceHub.GetAgentPresence(userId);
        var activity = await _service.BuildForUserAsync(userId, presence, ct);
        if (activity is null) return;

        // Group connections by recipient so queue access is resolved once
        // per user (cached anyway), then send a masked copy to each
        // recipient's connection(s).
        foreach (var byUser in subscribers.GroupBy(s => s.Value.UserId))
        {
            var role = byUser.First().Value.Role;
            var scope = await _queueAccess.GetScopeAsync(byUser.Key, role, ct);
            var masked = AgentActivityMasking.ForViewer(activity, scope);
            var connectionIds = byUser.Select(s => s.Key).ToArray();
            await _hub.Clients.Clients(connectionIds).SendAsync("AgentActivity", masked, ct);
        }
    }
}
