namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// SignalR / cache-invalidation broadcast emitted after a successful
/// TRMM sync run. Default implementation is the no-op; the Api project
/// substitutes a SignalR-backed broadcaster on the
/// <c>TicketPresenceHub</c> so the Assets page can react in real time.
public interface ITrmmSyncNotifier
{
    Task NotifyAssetsChangedAsync(TrmmSyncOutcome outcome, CancellationToken ct);
}

public sealed class NullTrmmSyncNotifier : ITrmmSyncNotifier
{
    public Task NotifyAssetsChangedAsync(TrmmSyncOutcome outcome, CancellationToken ct) =>
        Task.CompletedTask;
}
