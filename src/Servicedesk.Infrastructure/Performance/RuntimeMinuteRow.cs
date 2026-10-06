using System.Diagnostics;

namespace Servicedesk.Infrastructure.Performance;

/// Folds a minute's 10-second runtime/host samples into one row (averages
/// for levels, maxima for peaks, sums for counters). Host fields are null
/// when no sample carried host data (collector off / non-Linux).
public sealed class RuntimeMinuteRow
{
    public DateTime BucketUtc { get; set; }
    public string AppVersion { get; set; } = "";
    public string Level { get; set; } = "";
    public int Samples { get; set; }
    public long Requests { get; set; }
    public long InFlightMax { get; set; }
    public int ActiveUsers { get; set; }
    public double OverheadMs { get; set; }
    public double? CpuPct { get; set; }
    public double? CpuPctMax { get; set; }
    public double WorkingSetMb { get; set; }
    public double GcHeapMb { get; set; }
    public double GcHeapMbMax { get; set; }
    public double AllocMb { get; set; }
    public long Gen0 { get; set; }
    public long Gen1 { get; set; }
    public long Gen2 { get; set; }
    public double GcPauseMs { get; set; }
    public double IntervalMs { get; set; }
    public double TpThreadsAvg { get; set; }
    public int TpThreadsMax { get; set; }
    public double TpQueueAvg { get; set; }
    public long TpQueueMax { get; set; }
    public int TpQueueSamples { get; set; }
    public long Exceptions { get; set; }
    public long LockContentions { get; set; }
    public long KestrelActiveMax { get; set; }
    public long KestrelQueuedMax { get; set; }
    public long SignalRConnMax { get; set; }
    public double PoolBusyAvg { get; set; }
    public long PoolBusyMax { get; set; }
    public double PoolIdleAvg { get; set; }
    public long PoolMax { get; set; }
    public long PoolPendingMax { get; set; }
    public int PoolWaitSamples { get; set; }
    public long PoolTimeouts { get; set; }
    public double? HostCpuPct { get; set; }
    public double? HostCpuMax { get; set; }
    public double? HostStealPct { get; set; }
    public double? HostIoWaitPct { get; set; }
    public double? Load1 { get; set; }
    public int? HostCores { get; set; }
    public double? MemTotalMb { get; set; }
    public double? MemAvailableMb { get; set; }
    public double? SwapUsedMb { get; set; }
    public double? SwapIn { get; set; }
    public double? SwapOut { get; set; }
    public double? DiskAwaitMs { get; set; }
    public double? DiskUtilPct { get; set; }
    public double? CgThrottledPct { get; set; }
    public double? CgMemMb { get; set; }
    public double? CgMemLimitMb { get; set; }
    public long? OomKills { get; set; }
    public double? RootFreePct { get; set; }
    public double? BlobFreePct { get; set; }
    public double? RootFreeGb { get; set; }
    public double? BlobFreeGb { get; set; }

    public static RuntimeMinuteRow From(PerfWindow window, DateTime bucketUtc, string level, string version)
    {
        var samples = window.RuntimeSamples;
        var row = new RuntimeMinuteRow
        {
            BucketUtc = bucketUtc,
            AppVersion = version,
            Level = level,
            Samples = samples.Count,
            Requests = Interlocked.Read(ref window.Requests),
            InFlightMax = Interlocked.Read(ref window.InFlightMax),
            ActiveUsers = window.ActiveUsers.Count,
            OverheadMs = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref window.OverheadTicks)).TotalMilliseconds,
        };
        if (samples.Count == 0) return row;

        var cpu = samples.Where(s => s.ProcessCpuPct.HasValue).Select(s => s.ProcessCpuPct!.Value).ToArray();
        row.CpuPct = cpu.Length > 0 ? cpu.Average() : null;
        row.CpuPctMax = cpu.Length > 0 ? cpu.Max() : null;
        row.WorkingSetMb = samples.Average(s => s.WorkingSetMb);
        row.GcHeapMb = samples.Average(s => s.GcHeapMb);
        row.GcHeapMbMax = samples.Max(s => s.GcHeapMb);
        row.AllocMb = samples.Sum(s => s.AllocatedMb);
        row.Gen0 = samples.Sum(s => s.Gen0);
        row.Gen1 = samples.Sum(s => s.Gen1);
        row.Gen2 = samples.Sum(s => s.Gen2);
        row.GcPauseMs = samples.Sum(s => s.GcPauseMs);
        row.IntervalMs = samples.Sum(s => s.IntervalMs);
        row.TpThreadsAvg = samples.Average(s => s.ThreadPoolThreads);
        row.TpThreadsMax = samples.Max(s => s.ThreadPoolThreads);
        row.TpQueueAvg = samples.Average(s => s.ThreadPoolQueue);
        row.TpQueueMax = samples.Max(s => s.ThreadPoolQueue);
        row.TpQueueSamples = samples.Count(s => s.ThreadPoolQueue > 0);
        row.Exceptions = samples.Sum(s => s.Exceptions);
        row.LockContentions = samples.Sum(s => s.LockContentions);
        row.KestrelActiveMax = samples.Max(s => s.KestrelActive);
        row.KestrelQueuedMax = samples.Max(s => s.KestrelQueued);
        row.SignalRConnMax = samples.Max(s => s.SignalRConnections);
        row.PoolBusyAvg = samples.Average(s => s.PoolBusy);
        row.PoolBusyMax = samples.Max(s => s.PoolBusy);
        row.PoolIdleAvg = samples.Average(s => s.PoolIdle);
        row.PoolMax = samples.Max(s => s.PoolMax);
        row.PoolPendingMax = samples.Max(s => s.PoolPending);
        row.PoolWaitSamples = samples.Count(s => s.PoolPending > 0);
        row.PoolTimeouts = samples.Sum(s => s.PoolTimeouts);
        row.InFlightMax = Math.Max(row.InFlightMax, samples.Max(s => s.InFlight));

        var hosts = samples.Where(s => s.Host is not null).Select(s => s.Host!).ToArray();
        if (hosts.Length > 0)
        {
            row.HostCpuPct = Avg(hosts, h => h.CpuPct);
            row.HostCpuMax = Max(hosts, h => h.CpuPct);
            row.HostStealPct = Avg(hosts, h => h.StealPct);
            row.HostIoWaitPct = Avg(hosts, h => h.IoWaitPct);
            row.Load1 = Avg(hosts, h => h.Load1);
            row.HostCores = hosts.Select(h => h.HostCores).LastOrDefault(c => c.HasValue);
            row.MemTotalMb = Max(hosts, h => h.MemTotalMb);
            row.MemAvailableMb = Min(hosts, h => h.MemAvailableMb);
            row.SwapUsedMb = Max(hosts, h => h.SwapUsedMb);
            row.SwapIn = Sum(hosts, h => h.SwapInPages);
            row.SwapOut = Sum(hosts, h => h.SwapOutPages);
            row.DiskAwaitMs = Avg(hosts, h => h.DiskAwaitMs);
            row.DiskUtilPct = Max(hosts, h => h.DiskUtilPct);
            row.CgThrottledPct = Avg(hosts, h => h.CgroupThrottledPct);
            row.CgMemMb = Max(hosts, h => h.CgroupMemMb);
            row.CgMemLimitMb = Max(hosts, h => h.CgroupMemLimitMb);
            row.OomKills = hosts.Select(h => h.OomKills).LastOrDefault(v => v.HasValue);
            row.RootFreePct = Min(hosts, h => h.RootFreePct);
            row.BlobFreePct = Min(hosts, h => h.BlobFreePct);
            row.RootFreeGb = Min(hosts, h => h.RootFreeGb);
            row.BlobFreeGb = Min(hosts, h => h.BlobFreeGb);
        }
        return row;
    }

    private static double? Avg(HostSample[] hosts, Func<HostSample, double?> f)
    {
        var v = hosts.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return v.Length == 0 ? null : v.Average();
    }

    private static double? Max(HostSample[] hosts, Func<HostSample, double?> f)
    {
        var v = hosts.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return v.Length == 0 ? null : v.Max();
    }

    private static double? Min(HostSample[] hosts, Func<HostSample, double?> f)
    {
        var v = hosts.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return v.Length == 0 ? null : v.Min();
    }

    private static double? Sum(HostSample[] hosts, Func<HostSample, double?> f)
    {
        var v = hosts.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return v.Length == 0 ? null : v.Sum();
    }
}
