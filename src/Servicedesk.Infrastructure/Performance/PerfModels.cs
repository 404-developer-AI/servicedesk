namespace Servicedesk.Infrastructure.Performance;

/// Resolved data source for a period: short periods read the minute
/// tables; longer ones read the hourly rollups for every hour already
/// rolled up plus the minute tables for the remainder.
public sealed record PerfPeriod(
    DateTimeOffset From,
    DateTimeOffset To,
    bool UsesHourly,
    DateTime HourFrom,
    DateTime HourTo,
    DateTime MinuteFrom,
    DateTime MinuteTo,
    int StepSeconds)
{
    public TimeSpan Span => To - From;
}

public sealed class HttpRouteStats
{
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public long Count { get; set; }
    public long Errors { get; set; }
    public long Count4xx { get; set; }
    public long Count429 { get; set; }
    public double SumMs { get; set; }
    public double MaxMs { get; set; }
    public long[] Hist { get; set; } = Array.Empty<long>();
    public long DbCount { get; set; }
    public double DbMs { get; set; }
    public double ExtMs { get; set; }
    public double PipelineMs { get; set; }
    public long Bytes { get; set; }
    public long[] SizeHist { get; set; } = Array.Empty<long>();
    public int Users { get; set; }

    public double P50 => PerfHistogram.Percentile(Hist, 50, MaxMs);
    public double P95 => PerfHistogram.Percentile(Hist, 95, MaxMs);
    public double P99 => PerfHistogram.Percentile(Hist, 99, MaxMs);
    public double AvgMs => Count == 0 ? 0 : SumMs / Count;
    public double AvgDbMs => Count == 0 ? 0 : DbMs / Count;
    public double AvgExtMs => Count == 0 ? 0 : ExtMs / Count;
    public double AvgPipelineMs => Count == 0 ? 0 : PipelineMs / Count;
    public double AvgAppMs => Math.Max(0, AvgMs - AvgDbMs - AvgExtMs - AvgPipelineMs);
    public double AvgDbQueries => Count == 0 ? 0 : (double)DbCount / Count;
    public double AvgKb => Count == 0 ? 0 : Bytes / 1024.0 / Count;
    public double P95Kb => PerfHistogram.Percentile(SizeHist, 95);
    // v0.1.25 — capped at 100: a request that runs queries in parallel
    // (global search) can wait on the database longer than its own duration.
    public double DbSharePct => SumMs <= 0 ? 0 : Math.Min(100, 100 * DbMs / SumMs);
    public double ExtSharePct => SumMs <= 0 ? 0 : Math.Min(100, 100 * ExtMs / SumMs);
    public double PipelineSharePct => SumMs <= 0 ? 0 : 100 * PipelineMs / SumMs;
    public double AppSharePct => Math.Max(0, 100 - DbSharePct - ExtSharePct - PipelineSharePct);
    public string Key => Method + " " + Route;
}

public sealed class TimePoint
{
    public DateTime T { get; set; }
    public long Count { get; set; }
    public long Errors { get; set; }
    public double SumMs { get; set; }
    public double MaxMs { get; set; }
    public long[] Hist { get; set; } = Array.Empty<long>();
    public double DbMs { get; set; }
    public double ExtMs { get; set; }
    public int Users { get; set; }

    public double P50 => PerfHistogram.Percentile(Hist, 50, MaxMs);
    public double P95 => PerfHistogram.Percentile(Hist, 95, MaxMs);
    public double P99 => PerfHistogram.Percentile(Hist, 99, MaxMs);
}

public sealed class DbQueryStats
{
    public string Fingerprint { get; set; } = "";
    public string Sql { get; set; } = "";
    public string? Caller { get; set; }
    public long Count { get; set; }
    public long Errors { get; set; }
    public double SumMs { get; set; }
    public double MaxMs { get; set; }
    public long[] Hist { get; set; } = Array.Empty<long>();
    public List<SourceShare> Sources { get; set; } = new();

    public double AvgMs => Count == 0 ? 0 : SumMs / Count;
    public double P95 => PerfHistogram.Percentile(Hist, 95, MaxMs);
}

public sealed class SourceShare
{
    public string Fingerprint { get; set; } = "";
    public string Source { get; set; } = "";
    public long Count { get; set; }
    public double SumMs { get; set; }
}

public sealed class PgStatementDelta
{
    public long QueryId { get; set; }
    public string Query { get; set; } = "";
    public long Calls { get; set; }
    public double TotalMs { get; set; }
    public long Rows { get; set; }
    public long BlksHit { get; set; }
    public long BlksRead { get; set; }
    public long TempWritten { get; set; }
    public double StddevMs { get; set; }
    public double MaxMs { get; set; }

    public double MeanMs => Calls == 0 ? 0 : TotalMs / Calls;
    public double HitPct => BlksHit + BlksRead == 0 ? 100 : 100.0 * BlksHit / (BlksHit + BlksRead);
}

public sealed class PgStatementSnapshotRow
{
    public DateTime TsUtc { get; set; }
    public long QueryId { get; set; }
    public long Calls { get; set; }
    public double TotalMs { get; set; }
    public long Rows { get; set; }
    public long BlksHit { get; set; }
    public long BlksRead { get; set; }
    public long TempWritten { get; set; }
    public double StddevMs { get; set; }
    public double MaxMs { get; set; }
}

public sealed class TableDelta
{
    public string Name { get; set; } = "";
    public long Rows { get; set; }
    public long Dead { get; set; }
    public long SeqScan { get; set; }
    public long SeqTupRead { get; set; }
    public long IdxScan { get; set; }
    public long Writes { get; set; }
    public long TotalBytes { get; set; }
    public long BytesGrowth { get; set; }
    public long RowsGrowth { get; set; }
    public double? HitPct { get; set; }
    /// v0.1.26 — false when no snapshot exists at/before the period start:
    /// the delta columns are then 0 ("no data"), never the cumulative
    /// counters since the statistics reset.
    public bool HasBaseline { get; set; }
}

public sealed class GrowthPoint
{
    public string Name { get; set; } = "";
    public DateTime T { get; set; }
    public long TotalBytes { get; set; }
    public long Rows { get; set; }
}

public sealed class RuntimeSummary
{
    public long Minutes { get; set; }
    public long DiagnoseMinutes { get; set; }
    public long Requests { get; set; }
    public double OverheadMs { get; set; }
    public int ActiveUsersPeak { get; set; }
    public double? CpuPct { get; set; }
    public double? CpuPctMax { get; set; }
    public double? WorkingSetMb { get; set; }
    public double? GcHeapMbStart { get; set; }
    public double? GcHeapMbEnd { get; set; }
    public double? GcHeapMbMax { get; set; }
    public double GcPauseMs { get; set; }
    public double IntervalMs { get; set; }
    public long Gen2 { get; set; }
    public long Samples { get; set; }
    public long TpQueueSamples { get; set; }
    public double? TpThreadsStart { get; set; }
    public double? TpThreadsEnd { get; set; }
    public int TpThreadsMax { get; set; }
    public long Exceptions { get; set; }
    public long LockContentions { get; set; }
    public long PoolWaitSamples { get; set; }
    public long PoolPendingMax { get; set; }
    public long PoolTimeouts { get; set; }
    public long PoolMax { get; set; }
    public long PoolBusyMax { get; set; }
    public long InFlightMax { get; set; }
    public long KestrelQueuedMax { get; set; }
    public long SignalRConnMax { get; set; }
    public double? HostCpuPct { get; set; }
    public double? HostCpuMax { get; set; }
    public long HostCpuHighMinutes { get; set; }
    public long HostMinutes { get; set; }
    public double? StealPct { get; set; }
    public double? StealMax { get; set; }
    public double? IoWaitPct { get; set; }
    public double? Load1 { get; set; }
    public double? Load1Max { get; set; }
    public long LoadHighMinutes { get; set; }
    public int? HostCores { get; set; }
    public double? MemTotalMb { get; set; }
    public double? MemAvailableMinMb { get; set; }
    public double? SwapUsedMaxMb { get; set; }
    public double? SwapActivity { get; set; }
    public double? DiskAwaitMs { get; set; }
    public double? DiskAwaitMax { get; set; }
    public double? DiskUtilMax { get; set; }
    public double? DiskUtilAvg { get; set; }
    public double? CgThrottledPct { get; set; }
    public double? CgMemLimitMb { get; set; }
    public long? OomKillsDelta { get; set; }
    public double? RootFreePct { get; set; }
    public double? BlobFreePct { get; set; }
    public double? RootFreeGb { get; set; }
    public double? BlobFreeGb { get; set; }

    public double GcPausePct => IntervalMs <= 0 ? 0 : 100 * GcPauseMs / IntervalMs;
    public double ThreadPoolQueuePct => Samples == 0 ? 0 : 100.0 * TpQueueSamples / Samples;
    public double PoolWaitPct => Samples == 0 ? 0 : 100.0 * PoolWaitSamples / Samples;
    public double OverheadMsPerRequest => Requests == 0 ? 0 : OverheadMs / Requests;
}

public sealed class RuntimePoint
{
    public DateTime T { get; set; }
    public double? CpuPct { get; set; }
    public double? HostCpuPct { get; set; }
    public double? StealPct { get; set; }
    public double? IoWaitPct { get; set; }
    public double? Load1 { get; set; }
    public double? MemAvailableMb { get; set; }
    public double? MemTotalMb { get; set; }
    public double? SwapUsedMb { get; set; }
    public double? DiskAwaitMs { get; set; }
    public double? DiskUtilPct { get; set; }
    public double? WorkingSetMb { get; set; }
    public double? GcHeapMb { get; set; }
    public double? GcPausePct { get; set; }
    public double? TpThreads { get; set; }
    public double? TpQueue { get; set; }
    public double? PoolBusy { get; set; }
    public double? PoolMax { get; set; }
    public double? PoolPending { get; set; }
    public double? KestrelActive { get; set; }
    public double? SignalR { get; set; }
    public double? Exceptions { get; set; }
    public double? InFlight { get; set; }
    public double? Requests { get; set; }
    public double? OverheadMs { get; set; }
}

public sealed class SpanStats
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public long Count { get; set; }
    public long Errors { get; set; }
    public double SumMs { get; set; }
    public double MaxMs { get; set; }
    public long[] Hist { get; set; } = Array.Empty<long>();

    public double AvgMs => Count == 0 ? 0 : SumMs / Count;
    public double P50 => PerfHistogram.Percentile(Hist, 50, MaxMs);
    public double P95 => PerfHistogram.Percentile(Hist, 95, MaxMs);
}

public sealed class RumStats
{
    public string Route { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Detail { get; set; } = "";
    public long Count { get; set; }
    public double SumValue { get; set; }
    public double MaxValue { get; set; }
    public long[] Hist { get; set; } = Array.Empty<long>();

    public double Avg => Count == 0 ? 0 : SumValue / Count;
    public double P75 => PerfHistogram.Percentile(Hist, 75, MaxValue);
    public double P95 => PerfHistogram.Percentile(Hist, 95, MaxValue);
}

public sealed class RumBreakdown
{
    public string Dimension { get; set; } = "";
    public string Value { get; set; } = "";
    public long Count { get; set; }
}

public sealed class WorkerRunRow
{
    public DateTime StartedUtc { get; set; }
    public string Worker { get; set; } = "";
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public long Items { get; set; }
    public string? ErrorKind { get; set; }
}

public sealed class WorkerSummary
{
    public string Worker { get; set; } = "";
    public long Runs { get; set; }
    public long Failures { get; set; }
    public double AvgMs { get; set; }
    public double MaxMs { get; set; }
    public double TotalMs { get; set; }
    public long Items { get; set; }
    public DateTime LastRunUtc { get; set; }
}

public sealed class NPlusOneStats
{
    public string Route { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? Sql { get; set; }
    public string? Caller { get; set; }
    public long Requests { get; set; }
    public long Executions { get; set; }
    public long MaxPerRequest { get; set; }
}

public sealed class SlowRequestRow
{
    public long Id { get; set; }
    public DateTime TsUtc { get; set; }
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public int Status { get; set; }
    public double TotalMs { get; set; }
    public double DbMs { get; set; }
    public long DbCount { get; set; }
    public double ExtMs { get; set; }
    public double PipelineMs { get; set; }
    public int GcCount { get; set; }
    public long Bytes { get; set; }
    public string? Breakdown { get; set; }
    public string TraceId { get; set; } = "";
}

public sealed class MarkerRow
{
    public long Id { get; set; }
    public DateTime TsUtc { get; set; }
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string? CreatedBy { get; set; }
}

public sealed class VersionRange
{
    public string Version { get; set; } = "";
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }
    public long Minutes { get; set; }
}

public sealed class RouteComparison
{
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public long BeforeCount { get; set; }
    public double BeforeP95 { get; set; }
    public long AfterCount { get; set; }
    public double AfterP95 { get; set; }

    public double ChangePct => BeforeP95 <= 0 ? 0 : 100 * (AfterP95 - BeforeP95) / BeforeP95;
}

public sealed class DbPeriodStats
{
    public Dictionary<string, double?> Start { get; set; } = new();
    public Dictionary<string, double?> End { get; set; } = new();
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double? IdleInTxMaxS { get; set; }
    public double? BlockedMax { get; set; }
    public double? LongestActiveMaxS { get; set; }
    public double? ConnTotalMax { get; set; }

    public double? Delta(string key)
    {
        if (!End.TryGetValue(key, out var end) || end is null) return null;
        if (!Start.TryGetValue(key, out var start) || start is null) return null;
        var reset = End.GetValueOrDefault("stats_reset_epoch") != Start.GetValueOrDefault("stats_reset_epoch");
        return reset || end < start ? end : end - start;
    }

    public double? Latest(string key) => End.TryGetValue(key, out var v) ? v : null;

    public double? CacheHitPct
    {
        get
        {
            var hit = Delta("blks_hit");
            var read = Delta("blks_read");
            if (hit is null || read is null || hit + read <= 0) return null;
            return 100 * hit / (hit + read);
        }
    }
}

public sealed class SeriesValue
{
    public DateTime T { get; set; }
    public double Value { get; set; }
}
