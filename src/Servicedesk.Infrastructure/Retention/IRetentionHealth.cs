namespace Servicedesk.Infrastructure.Retention;

/// v0.0.101 — in-memory state of the data-retention worker, surfaced on the
/// admin Health page like every other background subsystem (three-layer
/// health flow; see ARCHITECTURE → Observability & health).
public interface IRetentionHealth
{
    RetentionHealthSnapshot Snapshot();
    void RecordRun(IReadOnlyDictionary<string, long> deletedPerTable, TimeSpan duration, DateTime nextRunUtc);
    void RecordFailure(string error);
}

public sealed record RetentionHealthSnapshot(
    DateTime? LastRunUtc,
    DateTime? NextRunUtc,
    TimeSpan? LastDuration,
    IReadOnlyDictionary<string, long> LastDeletedPerTable,
    long TotalDeletedSinceStart,
    string? LastError,
    DateTime? LastErrorUtc);

public sealed class RetentionHealth : IRetentionHealth
{
    private readonly object _gate = new();
    private DateTime? _lastRunUtc;
    private DateTime? _nextRunUtc;
    private TimeSpan? _lastDuration;
    private IReadOnlyDictionary<string, long> _lastDeleted = new Dictionary<string, long>();
    private long _total;
    private string? _lastError;
    private DateTime? _lastErrorUtc;

    public RetentionHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RetentionHealthSnapshot(_lastRunUtc, _nextRunUtc, _lastDuration, _lastDeleted, _total, _lastError, _lastErrorUtc);
        }
    }

    public void RecordRun(IReadOnlyDictionary<string, long> deletedPerTable, TimeSpan duration, DateTime nextRunUtc)
    {
        lock (_gate)
        {
            _lastRunUtc = DateTime.UtcNow;
            _nextRunUtc = nextRunUtc;
            _lastDuration = duration;
            _lastDeleted = new Dictionary<string, long>(deletedPerTable);
            _total += deletedPerTable.Values.Sum();
            _lastError = null;
            _lastErrorUtc = null;
        }
    }

    public void RecordFailure(string error)
    {
        lock (_gate)
        {
            _lastError = error;
            _lastErrorUtc = DateTime.UtcNow;
        }
    }
}
