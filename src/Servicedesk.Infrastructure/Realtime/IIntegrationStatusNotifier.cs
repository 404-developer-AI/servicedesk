namespace Servicedesk.Infrastructure.Realtime;

/// Server→admin push for integration health changes (v0.0.25). Triggered by
/// the healthcheck BackgroundService and by AdsolutEndpoints' connect /
/// disconnect / refresh paths so a tile in the SPA flips immediately
/// instead of waiting for the 30-second poll interval. The payload is
/// deliberately minimal: just the integration key and the resolved state
/// string — the SPA uses it as a "stale, refetch" ping and re-runs its
/// existing GET /status query for the full payload, so we never ship
/// authorized-subject / email through the broadcast channel.
public interface IIntegrationStatusNotifier
{
    Task NotifyStatusChangedAsync(string integration, string state, CancellationToken ct);
}

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullIntegrationStatusNotifier : IIntegrationStatusNotifier
{
    public Task NotifyStatusChangedAsync(string integration, string state, CancellationToken ct)
        => Task.CompletedTask;
}
