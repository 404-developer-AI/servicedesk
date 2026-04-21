namespace Servicedesk.Infrastructure.Realtime;

/// Push-channel for security-activity health alerts. Fan-out target is
/// "every active Admin" — fetched server-side so the publisher never has
/// to know which user-ids count. Pushed only on state transitions
/// (first time a category trips, or severity escalates) to keep the toast
/// firing rate sane on a sustained attack.
public interface ISecurityAlertNotifier
{
    Task NotifyAdminsAsync(SecurityAlertPush payload, CancellationToken ct);
}

public sealed record SecurityAlertPush(
    string Severity,
    string Subsystem,
    string Summary,
    long? IncidentId,
    DateTime CreatedUtc);

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullSecurityAlertNotifier : ISecurityAlertNotifier
{
    public Task NotifyAdminsAsync(SecurityAlertPush payload, CancellationToken ct)
        => Task.CompletedTask;
}
