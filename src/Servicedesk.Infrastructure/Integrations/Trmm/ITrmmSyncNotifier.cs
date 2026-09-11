namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// SignalR / cache-invalidation broadcast emitted after a successful
/// TRMM sync run. Default implementation is the no-op; the Api project
/// substitutes a SignalR-backed broadcaster on the
/// <c>TicketPresenceHub</c> so the Assets page can react in real time.
public interface ITrmmSyncNotifier
{
    Task NotifyAssetsChangedAsync(TrmmSyncOutcome outcome, CancellationToken ct);

    /// v0.1.10 — Remote Desktop tab invalidation: fired after an RDS sync
    /// cycle and after every client-note mutation. Rides the same
    /// <c>AssetsChanged</c> client event with a <c>kind</c> discriminator
    /// on the payload, so no new hub method has to be registered.
    Task NotifyRemoteDesktopChangedAsync(object payload, CancellationToken ct);
}

public sealed class NullTrmmSyncNotifier : ITrmmSyncNotifier
{
    public Task NotifyAssetsChangedAsync(TrmmSyncOutcome outcome, CancellationToken ct) =>
        Task.CompletedTask;

    public Task NotifyRemoteDesktopChangedAsync(object payload, CancellationToken ct) =>
        Task.CompletedTask;
}
