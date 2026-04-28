using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="IIntegrationStatusNotifier"/>.
/// Broadcasts to the integrations-group on <see cref="IntegrationsHub"/>;
/// the client uses each push as a "stale, refetch" signal and re-runs its
/// existing GET /status query for the full payload.
public sealed class SignalRIntegrationStatusNotifier : IIntegrationStatusNotifier
{
    private readonly IHubContext<IntegrationsHub> _hub;

    public SignalRIntegrationStatusNotifier(IHubContext<IntegrationsHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyStatusChangedAsync(string integration, string state, CancellationToken ct)
        => _hub.Clients.Group(IntegrationsHub.IntegrationsGroup)
            .SendAsync("IntegrationStatusUpdated", integration, state, ct);

    public Task NotifySyncCompletedAsync(string integration, CancellationToken ct)
        => _hub.Clients.Group(IntegrationsHub.IntegrationsGroup)
            .SendAsync("IntegrationSyncCompleted", integration, ct);
}
