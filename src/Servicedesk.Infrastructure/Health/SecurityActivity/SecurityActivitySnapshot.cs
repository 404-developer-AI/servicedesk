namespace Servicedesk.Infrastructure.Health.SecurityActivity;

/// Frozen result of one monitor evaluation. The HealthAggregator reads it
/// to render the security-activity card without re-querying the audit log
/// on every <c>/api/admin/health</c> poll.
///
/// <see cref="AcknowledgedFromUtc"/> is non-null when an admin acknowledged
/// an open incident; the monitor then only counts events that occurred
/// after that moment instead of the full rolling window. This avoids the
/// "ack and it immediately flips red again" footgun where the ack'd
/// events are still inside the window. The baseline is discarded once the
/// rolling window has fully rolled past it (i.e. all ack'd events have
/// naturally aged out).
public sealed record SecurityActivitySnapshot(
    DateTime EvaluatedUtc,
    TimeSpan Window,
    HealthStatus Status,
    string Summary,
    IReadOnlyList<SecurityActivityCategoryResult> Categories,
    bool MonitorEnabled,
    DateTime? AcknowledgedFromUtc = null);

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
/// Also holds two pieces of state that survive between ticks:
///   • the <em>alert guard</em> — the severity of the last alert we fired
///     — so the monitor only re-fires on upward transitions.
///   • the <em>ack baseline</em> — timestamp from which the monitor
///     should start counting after an admin acknowledged an incident,
///     so ack'd events don't immediately re-trigger the card.
/// Both live here (not on the monitor) so the subsystem-reset path can
/// update them atomically when an admin acknowledges.
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

    /// Timestamp from which the monitor should start counting events.
    /// <c>null</c> means "use the full rolling window". Set by
    /// <see cref="Acknowledge"/> and automatically cleared by the monitor
    /// once the rolling window has rolled past it.
    DateTime? GetAcknowledgedFromUtc();

    /// Writes the ack baseline. Called by the monitor to clear it
    /// (<c>null</c>) once the window has rolled past, and by
    /// <see cref="Acknowledge"/> to set a fresh one.
    void SetAcknowledgedFromUtc(DateTime? fromUtc);

    /// Marks the current episode as acknowledged at <paramref name="nowUtc"/>.
    /// Subsequent monitor ticks only count events with
    /// <c>audit.utc &gt;= nowUtc</c> until the rolling window has
    /// completely rolled past that moment. Resets the alert guard so a
    /// still-ongoing attack re-pages the admin via a fresh incident.
    /// The snapshot is dropped so the card shows a "Waiting…" state
    /// until the next tick produces a post-ack evaluation.
    void Acknowledge(DateTime nowUtc);

    /// Drops snapshot + alert guard + ack baseline. Used for full-subsystem
    /// resets (tests, disable-and-re-enable, etc.). Acknowledge is the
    /// production path for admin-triggered resets.
    void Clear();
}

public sealed class InMemorySecurityActivitySnapshot : ISecurityActivitySnapshot
{
    private SecurityActivitySnapshot? _current;
    private HealthStatus _lastAlerted = HealthStatus.Ok;
    private DateTime? _ackFromUtc;
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

    public DateTime? GetAcknowledgedFromUtc()
    {
        lock (_gate) return _ackFromUtc;
    }

    public void SetAcknowledgedFromUtc(DateTime? fromUtc)
    {
        lock (_gate) _ackFromUtc = fromUtc;
    }

    public void Acknowledge(DateTime nowUtc)
    {
        lock (_gate)
        {
            _current = null;
            _lastAlerted = HealthStatus.Ok;
            _ackFromUtc = nowUtc;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
            _lastAlerted = HealthStatus.Ok;
            _ackFromUtc = null;
        }
    }
}
