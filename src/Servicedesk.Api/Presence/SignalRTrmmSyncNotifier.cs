using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Integrations.Trmm;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="ITrmmSyncNotifier"/>.
/// Broadcasts an <c>AssetsChanged</c> ping to the <c>ticket-list</c>
/// group (every connected agent/admin) so the Assets page can
/// invalidate its React Query cache the moment a sync run finishes.
/// The payload carries the per-table counts so a viewer can render a
/// "X agents updated" toast without an extra round-trip.
///
/// v0.1.10: the Remote Desktop tab (RDS sync + client notes) reuses the
/// same event name with a <c>kind</c> discriminator on the payload.
public sealed class SignalRTrmmSyncNotifier : ITrmmSyncNotifier
{
    private readonly IHubContext<TicketPresenceHub> _hub;

    public SignalRTrmmSyncNotifier(IHubContext<TicketPresenceHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyAssetsChangedAsync(TrmmSyncOutcome outcome, CancellationToken ct) =>
        _hub.Clients.Group("ticket-list").SendAsync(
            "AssetsChanged",
            new
            {
                kind = "agents-sync",
                clients = outcome.Clients,
                sites = outcome.Sites,
                agents = outcome.Agents,
                autoLinkedCompanies = outcome.AutoLinkedCompanies,
                latencyMs = outcome.LatencyMs,
            },
            ct);

    public Task NotifyRemoteDesktopChangedAsync(object payload, CancellationToken ct) =>
        _hub.Clients.Group("ticket-list").SendAsync("AssetsChanged", payload, ct);
}
