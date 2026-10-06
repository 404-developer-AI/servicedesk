using System.Diagnostics;

namespace Servicedesk.Infrastructure.Performance;

/// One 10-second reading of the .NET process (+ the host when enabled).
/// Counter fields (Gen*, Exceptions, …) are deltas since the previous sample.
public sealed record RuntimeSample(
    DateTimeOffset TimestampUtc,
    double? ProcessCpuPct,
    double WorkingSetMb,
    double GcHeapMb,
    double AllocatedMb,
    long Gen0,
    long Gen1,
    long Gen2,
    double GcPauseMs,
    double IntervalMs,
    int ThreadPoolThreads,
    long ThreadPoolQueue,
    long Exceptions,
    long LockContentions,
    long KestrelActive,
    long KestrelQueued,
    long SignalRConnections,
    long PoolBusy,
    long PoolIdle,
    long PoolMax,
    long PoolPending,
    long PoolTimeouts,
    long InFlight,
    HostSample? Host);

/// Cheap .NET runtime probes (all O(1) API calls, no EventListener): the
/// .NET 8 runtime has no System.Runtime meter yet, and EventCounters would
/// add a listener thread for numbers these calls return directly.
public sealed class RuntimeSampler
{
    private static long _firstChanceExceptions;
    private static int _subscribed;

    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _prevCpu;
    private long _prevTimestamp;
    private int _prevGen0, _prevGen1, _prevGen2;
    private TimeSpan _prevPause;
    private long _prevAllocated;
    private long _prevExceptions;
    private long _prevContentions;
    private long _prevPoolTimeouts;
    private bool _primed;

    public RuntimeSampler()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, _) => Interlocked.Increment(ref _firstChanceExceptions);
        }
    }

    public RuntimeSample Sample(DateTimeOffset nowUtc, PerfGauges gauges, long inFlight, HostSample? host)
    {
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var timestamp = Stopwatch.GetTimestamp();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        var allocated = GC.GetTotalAllocatedBytes(false);
        var exceptions = Interlocked.Read(ref _firstChanceExceptions);
        var contentions = Monitor.LockContentionCount;
        var poolTimeouts = Volatile.Read(ref gauges.PoolTimeouts);
        var info = GC.GetGCMemoryInfo();

        double? cpuPct = null;
        double intervalMs = 0;
        if (_primed)
        {
            intervalMs = Stopwatch.GetElapsedTime(_prevTimestamp, timestamp).TotalMilliseconds;
            if (intervalMs > 0)
            {
                cpuPct = Math.Clamp(100.0 * (cpu - _prevCpu).TotalMilliseconds / (intervalMs * Environment.ProcessorCount), 0, 100);
            }
        }

        var sample = new RuntimeSample(
            nowUtc,
            cpuPct,
            Environment.WorkingSet / 1024.0 / 1024.0,
            info.HeapSizeBytes / 1024.0 / 1024.0,
            _primed ? (allocated - _prevAllocated) / 1024.0 / 1024.0 : 0,
            _primed ? gen0 - _prevGen0 : 0,
            _primed ? gen1 - _prevGen1 : 0,
            _primed ? gen2 - _prevGen2 : 0,
            _primed ? (pause - _prevPause).TotalMilliseconds : 0,
            intervalMs,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            _primed ? exceptions - _prevExceptions : 0,
            _primed ? contentions - _prevContentions : 0,
            Volatile.Read(ref gauges.KestrelActive),
            Volatile.Read(ref gauges.KestrelQueued),
            Volatile.Read(ref gauges.SignalRConnections),
            Volatile.Read(ref gauges.PoolUsed),
            Volatile.Read(ref gauges.PoolIdle),
            Volatile.Read(ref gauges.PoolMax),
            Math.Max(0, Volatile.Read(ref gauges.PoolPending)),
            _primed ? poolTimeouts - _prevPoolTimeouts : 0,
            inFlight,
            host);

        _prevCpu = cpu;
        _prevTimestamp = timestamp;
        _prevGen0 = gen0;
        _prevGen1 = gen1;
        _prevGen2 = gen2;
        _prevPause = pause;
        _prevAllocated = allocated;
        _prevExceptions = exceptions;
        _prevContentions = contentions;
        _prevPoolTimeouts = poolTimeouts;
        _primed = true;
        return sample;
    }
}
