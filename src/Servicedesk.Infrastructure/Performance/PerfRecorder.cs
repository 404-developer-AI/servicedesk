using System.Diagnostics;

namespace Servicedesk.Infrastructure.Performance;

/// Owner of the current in-memory measurement window. Collectors only ever
/// touch <see cref="Current"/>; <see cref="PerfFlushService"/> swaps it once
/// a minute and writes the retired window to Postgres in one batch — the
/// request path never waits on the database for monitoring.
public sealed class PerfRecorder
{
    private PerfWindow _current;
    private long _inFlight;

    public PerfRecorder(TimeProvider time)
    {
        _current = new PerfWindow(time.GetUtcNow());
        PerfRuntime.Recorder = this;
    }

    public PerfWindow Current => Volatile.Read(ref _current);

    /// Installs a fresh window and returns the retired one.
    public PerfWindow Swap(DateTimeOffset nowUtc) => Interlocked.Exchange(ref _current, new PerfWindow(nowUtc));

    public void RequestStarted()
    {
        var now = Interlocked.Increment(ref _inFlight);
        var window = Current;
        Interlocked.Increment(ref window.Requests);
        PerfAggregate.UpdateMax(ref window.InFlightMax, now);
    }

    public void RequestEnded() => Interlocked.Decrement(ref _inFlight);

    public long InFlight => Volatile.Read(ref _inFlight);

    /// Time the collectors themselves spent (Stopwatch ticks) — shown on the
    /// dashboard as the cost of monitoring.
    public void AddOverhead(long stopwatchTicks) => Interlocked.Add(ref Current.OverheadTicks, stopwatchTicks);

    public static long Now() => Stopwatch.GetTimestamp();
}

/// Static access for code that is not resolved through DI (the search-source
/// decorator, worker loops, Serilog-style bridges). Null until the
/// singletons are constructed — every caller treats null as "monitoring off".
public static class PerfRuntime
{
    public static PerfRecorder? Recorder { get; internal set; }
    public static IPerfSettings? Settings { get; internal set; }

    /// True when <paramref name="collector"/> is active right now.
    public static bool IsOn(PerfCollector collector) => Settings?.IsEnabled(collector) == true && Recorder is not null;
}
