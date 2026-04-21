namespace Servicedesk.Infrastructure.Health.SecurityActivity;

/// Frozen result of one monitor evaluation. The HealthAggregator reads it
/// to render the security-activity card without re-querying the audit log
/// on every <c>/api/admin/health</c> poll.
public sealed record SecurityActivitySnapshot(
    DateTime EvaluatedUtc,
    TimeSpan Window,
    HealthStatus Status,
    string Summary,
    IReadOnlyList<SecurityActivityCategoryResult> Categories,
    bool MonitorEnabled);

/// Per-category outcome inside a snapshot.
public sealed record SecurityActivityCategoryResult(
    string Key,
    string Label,
    int Count,
    int Threshold,
    int CriticalThreshold,
    HealthStatus Status);

/// In-memory holder for the latest snapshot. The monitor writes here on
/// every evaluation tick; the aggregator reads. Singleton in DI.
///
/// Also holds the <em>alert guard</em> — the severity of the last alert we
/// already fired — so the monitor only re-fires on upward transitions. The
/// guard lives here (not on the monitor) so <see cref="Clear"/> from the
/// subsystem-reset path also resets it: after an admin acknowledges open
/// incidents, the next still-hot tick must page them again.
public interface ISecurityActivitySnapshot
{
    /// Returns the latest snapshot, or <c>null</c> if the monitor has not
    /// yet completed a first evaluation pass.
    SecurityActivitySnapshot? Get();

    /// Replaces the snapshot atomically.
    void Set(SecurityActivitySnapshot snapshot);

    /// The severity of the last alert we fired. Used by the monitor to
    /// suppress duplicate alerts at the same severity. Defaults to
    /// <see cref="HealthStatus.Ok"/> (no alert fired yet).
    HealthStatus GetLastAlertedSeverity();

    /// Records the severity of an alert that was just fired.
    void SetLastAlertedSeverity(HealthStatus severity);

    /// Drops the cached snapshot + alert guard so the next tick starts
    /// from "no prior state". Used by the subsystem-reset path after an
    /// admin acknowledges all open security-activity incidents.
    void Clear();
}

public sealed class InMemorySecurityActivitySnapshot : ISecurityActivitySnapshot
{
    private SecurityActivitySnapshot? _current;
    private HealthStatus _lastAlerted = HealthStatus.Ok;
    private readonly object _gate = new();

    public SecurityActivitySnapshot? Get()
    {
        lock (_gate) return _current;
    }

    public void Set(SecurityActivitySnapshot snapshot)
    {
        lock (_gate) _current = snapshot;
    }

    public HealthStatus GetLastAlertedSeverity()
    {
        lock (_gate) return _lastAlerted;
    }

    public void SetLastAlertedSeverity(HealthStatus severity)
    {
        lock (_gate) _lastAlerted = severity;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
            _lastAlerted = HealthStatus.Ok;
        }
    }
}
