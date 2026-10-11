namespace Servicedesk.Infrastructure.Storage;

/// In-memory state of the blob GC, surfaced on the Health page (Blob storage
/// card) and used by the run-now action. Also the latest blob-store size
/// figures, which only a full scan can produce.
public interface IBlobGcHealth
{
    BlobGcSnapshot? LastRun { get; }
    string? LastError { get; }
    DateTime? LastErrorUtc { get; }
    DateTime? NextRunUtc { get; }
    void RecordRun(BlobGcSnapshot snapshot, DateTime nextRunUtc);
    void RecordFailure(string error, DateTime nextRunUtc);

    /// Wakes the worker for an immediate run (admin "Run now").
    void RequestRun();
    Task WaitForRunRequestAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed record BlobGcSnapshot(
    DateTime FinishedUtc,
    TimeSpan Duration,
    bool DryRun,
    long FilesScanned,
    long BytesScanned,
    long ReferencedHashes,
    long ReferencedMissing,
    long OrphanFiles,
    long OrphanBytes,
    long DeletedFiles,
    long DeletedBytes,
    int StaleTempFiles,
    long StaleTempBytes,
    long RawEmlExpired);

public sealed class BlobGcHealth : IBlobGcHealth
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private BlobGcSnapshot? _last;
    private string? _lastError;
    private DateTime? _lastErrorUtc;
    private DateTime? _nextRunUtc;

    public BlobGcSnapshot? LastRun { get { lock (_gate) return _last; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public DateTime? LastErrorUtc { get { lock (_gate) return _lastErrorUtc; } }
    public DateTime? NextRunUtc { get { lock (_gate) return _nextRunUtc; } }

    public void RecordRun(BlobGcSnapshot snapshot, DateTime nextRunUtc)
    {
        lock (_gate)
        {
            _last = snapshot;
            _lastError = null;
            _lastErrorUtc = null;
            _nextRunUtc = nextRunUtc;
        }
    }

    public void RecordFailure(string error, DateTime nextRunUtc)
    {
        lock (_gate)
        {
            _lastError = error;
            _lastErrorUtc = DateTime.UtcNow;
            _nextRunUtc = nextRunUtc;
        }
    }

    public void RequestRun()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a run is already requested */ }
    }

    public Task WaitForRunRequestAsync(TimeSpan timeout, CancellationToken ct) =>
        _wake.WaitAsync(timeout, ct);
}
