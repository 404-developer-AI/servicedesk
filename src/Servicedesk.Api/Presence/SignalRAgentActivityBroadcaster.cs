using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Dashboard;

namespace Servicedesk.Api.Presence;

/// Pushes per-user <see cref="AgentActivity"/> records to the
/// "agent-activity-admins" SignalR group. Used by the presence hub on
/// every viewing/recent change and by the Telavox polling worker on
/// every call-state edge so the dashboard tile receives a single
/// uniform stream of updates regardless of source.
public sealed class SignalRAgentActivityBroadcaster : IAgentActivityBroadcaster
{
    public const string AdminsGroup = "agent-activity-admins";

    private readonly IHubContext<TicketPresenceHub> _hub;
    private readonly IAgentActivityService _service;

    public SignalRAgentActivityBroadcaster(
        IHubContext<TicketPresenceHub> hub,
        IAgentActivityService service)
    {
        _hub = hub;
        _service = service;
    }

    public async Task BroadcastForUserAsync(Guid userId, CancellationToken ct)
    {
        var presence = TicketPresenceHub.GetAgentPresence(userId);
        var activity = await _service.BuildForUserAsync(userId, presence, ct);
        if (activity is null) return;
        await _hub.Clients.Group(AdminsGroup).SendAsync("AgentActivity", activity, ct);
    }
}
