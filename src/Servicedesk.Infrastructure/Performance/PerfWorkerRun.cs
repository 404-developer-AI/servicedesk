using System.Diagnostics;

namespace Servicedesk.Infrastructure.Performance;

/// One background-worker run, recorded for the Background tab and the
/// "latency spike while a worker ran" analysis. Usage inside a worker loop:
/// <code>
/// using var run = PerfWorkerRun.Start("mail-polling");
/// try { … run.AddItems(n); }
/// catch (Exception ex) { run.Fail(ex); … }
/// </code>
/// While the run is open, database queries on the same async flow are
/// attributed to <c>worker:mail-polling</c> instead of "other", so worker
/// load never blends into the API statistics. Static on purpose (same
/// pattern as the incident-log bridge): workers need no constructor change,
/// and with monitoring off <see cref="Start"/> costs one field read.
public sealed class PerfWorkerRun : IDisposable
{
    private static readonly PerfWorkerRun Noop = new(null, null, 0, false);

    /// Successful runs without items that finish faster than this are not recorded.
    public const double IdleRunMs = 25;

    private readonly string? _worker;
    private readonly string? _previousWorker;
    private readonly long _start;
    private readonly bool _active;
    private readonly DateTimeOffset _startedUtc;
    private long _items;
    private string? _error;
    private bool _disposed;

    private PerfWorkerRun(string? worker, string? previous, long start, bool active)
    {
        _worker = worker;
        _previousWorker = previous;
        _start = start;
        _active = active;
        _startedUtc = PerfRuntime.Settings?.Time.GetUtcNow() ?? DateTimeOffset.UtcNow;
    }

    public static PerfWorkerRun Start(string worker)
    {
        if (!PerfRuntime.IsOn(PerfCollector.Workers)) return Noop;
        var previous = PerfContext.Worker;
        PerfContext.Worker = worker;
        return new PerfWorkerRun(worker, previous, Stopwatch.GetTimestamp(), true);
    }

    public void AddItems(long count)
    {
        if (_active) Interlocked.Add(ref _items, count);
    }

    /// Marks the run failed. Only the exception <em>type</em> is kept — a
    /// message can carry customer data (addresses, subjects).
    public void Fail(Exception ex)
    {
        if (_active) _error = ex.GetType().Name;
    }

    public void Fail(string kind)
    {
        if (_active) _error = PerfRedactor.Text(kind.Length > 80 ? kind[..80] : kind);
    }

    public void Dispose()
    {
        if (!_active || _disposed) return;
        _disposed = true;
        PerfContext.Worker = _previousWorker;
        var recorder = PerfRuntime.Recorder;
        if (recorder is null) return;
        var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        // An idle poll (nothing to do, done in a blink) is noise, not a run.
        if (_error is null && _items == 0 && ms < IdleRunMs) return;
        recorder.Current.AddWorkerRun(new WorkerRunRecord(_startedUtc, _worker!, ms, _error is null, _items, _error));
    }
}
