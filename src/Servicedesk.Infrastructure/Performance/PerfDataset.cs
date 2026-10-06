using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

/// Everything the findings engine and the export need for one period,
/// gathered once. Pure data — the engine and the report builder are
/// deterministic functions of it, which keeps them unit-testable.
public sealed class PerfDataset
{
    public required PerfPeriod Period { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required PerfOptions Options { get; init; }
    public required PerfLevel Level { get; init; }
    public required FindingThresholds Thresholds { get; init; }

    public HttpRouteStats? Total { get; init; }
    public IReadOnlyList<HttpRouteStats> Routes { get; init; } = Array.Empty<HttpRouteStats>();
    public IReadOnlyList<TimePoint> Timeline { get; init; } = Array.Empty<TimePoint>();
    public IReadOnlyList<DbQueryStats> Queries { get; init; } = Array.Empty<DbQueryStats>();
    public IReadOnlyList<NPlusOneStats> NPlusOne { get; init; } = Array.Empty<NPlusOneStats>();
    public IReadOnlyList<PgStatementDelta> PgStatements { get; init; } = Array.Empty<PgStatementDelta>();
    public DbPeriodStats PgDatabase { get; init; } = new();
    public IReadOnlyList<TableDelta> TableDeltas { get; init; } = Array.Empty<TableDelta>();
    public IReadOnlyList<PgInspector.TableRow> Tables { get; init; } = Array.Empty<PgInspector.TableRow>();
    public IReadOnlyList<PgInspector.BloatRow> Bloat { get; init; } = Array.Empty<PgInspector.BloatRow>();
    public IReadOnlyList<PgInspector.IndexRow> Indexes { get; init; } = Array.Empty<PgInspector.IndexRow>();
    public IReadOnlyList<PgInspector.DuplicateIndexRow> DuplicateIndexes { get; init; } = Array.Empty<PgInspector.DuplicateIndexRow>();
    public IReadOnlyList<PgInspector.SettingRow> PgSettings { get; init; } = Array.Empty<PgInspector.SettingRow>();
    public RuntimeSummary Runtime { get; init; } = new();
    public IReadOnlyList<SpanStats> Spans { get; init; } = Array.Empty<SpanStats>();
    public IReadOnlyList<RumStats> Rum { get; init; } = Array.Empty<RumStats>();
    public IReadOnlyList<RumBreakdown> RumBreakdown { get; init; } = Array.Empty<RumBreakdown>();
    public IReadOnlyList<WorkerSummary> Workers { get; init; } = Array.Empty<WorkerSummary>();
    public IReadOnlyList<WorkerRunRow> WorkerRuns { get; init; } = Array.Empty<WorkerRunRow>();
    public IReadOnlyList<WorkerOverlap> WorkerOverlaps { get; init; } = Array.Empty<WorkerOverlap>();
    public IReadOnlyList<SlowRequestRow> SlowRequests { get; init; } = Array.Empty<SlowRequestRow>();
    public IReadOnlyList<MarkerRow> Markers { get; init; } = Array.Empty<MarkerRow>();
    public IReadOnlyList<VersionRange> Versions { get; init; } = Array.Empty<VersionRange>();
    public VersionComparisonResult? VersionComparison { get; init; }
    public IReadOnlyList<RouteComparison>? PeriodComparison { get; init; }
    public IReadOnlyList<Budget> Budgets { get; init; } = Array.Empty<Budget>();
    public PgAccess PgAccess { get; init; } = PgAccess.Unknown;
    public bool HostSupported { get; init; }
    public long MonitorStorageBytes { get; init; }
    public BundleInfo? Bundle { get; init; }
    public string AppVersion { get; init; } = PerfAppInfo.Version;
    public int MaxPoolSize { get; init; }

    /// Sections that could not be read (shown as "missing data" in the report).
    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public IEnumerable<SpanStats> SpansOf(string kind) => Spans.Where(s => s.Kind == kind);
    public IEnumerable<HttpRouteStats> ApiRoutes => Routes.Where(r => r.Route.StartsWith("/api/", StringComparison.Ordinal));
    public IEnumerable<RumStats> RumOf(string metric) => Rum.Where(r => r.Metric == metric);
}

public sealed record VersionComparisonResult(string Before, string After, IReadOnlyList<RouteComparison> Routes);

public sealed record WorkerOverlap(string Worker, long MinutesDuring, long RequestsDuring, double P95During, double P95Outside);

public sealed class Budget
{
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public int TargetMs { get; set; }
    public long Count { get; set; }
    public double P95 { get; set; }
    public bool HasData => Count > 0;
    public bool Ok => !HasData || P95 <= TargetMs;

    /// Parses "METHOD route=ms" lines (also ';'-separated). Invalid lines are skipped.
    public static IReadOnlyList<Budget> Parse(string? raw)
    {
        var result = new List<Budget>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var line in raw.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.LastIndexOf('=');
            if (eq <= 0) continue;
            if (!int.TryParse(line[(eq + 1)..].Trim(), out var ms) || ms <= 0) continue;
            var left = line[..eq].Trim();
            var space = left.IndexOf(' ');
            if (space <= 0) continue;
            var method = left[..space].Trim().ToUpperInvariant();
            var route = left[(space + 1)..].Trim();
            if (route.Length == 0 || !route.StartsWith('/')) continue;
            result.Add(new Budget { Method = method, Route = route.Length > 1 ? route.TrimEnd('/') : route, TargetMs = ms });
            if (result.Count >= 30) break;
        }
        return result;
    }
}

/// Vite chunk sizes written at build time (perf-bundle.json next to index.html).
public sealed class BundleInfo
{
    public DateTime? BuiltUtc { get; set; }
    public long TotalJsBytes { get; set; }
    public long TotalCssBytes { get; set; }
    public long TotalJsGzipBytes { get; set; }
    public List<BundleChunk> Chunks { get; set; } = new();
}

public sealed class BundleChunk
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public long Bytes { get; set; }
    public long GzipBytes { get; set; }
    public bool IsEntry { get; set; }
}

/// Finding thresholds (Settings → Performance → Thresholds).
public sealed class FindingThresholds
{
    public int RouteP95Ms { get; set; } = 1000;
    public int RouteMinRequests { get; set; } = 20;
    public int DbSharePct { get; set; } = 60;
    public int AppSharePct { get; set; } = 60;
    public int ExtSharePct { get; set; } = 50;
    public int NPlusOneMinRequests { get; set; } = 10;
    public int SlowQueryTopN { get; set; } = 5;
    public int SlowQueryMs { get; set; } = 200;
    public int SeqScanMinRows { get; set; } = 10_000;
    public int SeqScanRatio { get; set; } = 10;
    public int DeadTuplePct { get; set; } = 20;
    public int VacuumStaleDays { get; set; } = 7;
    public int CacheHitPct { get; set; } = 99;
    public int TempMb { get; set; } = 100;
    public int IdleInTxSeconds { get; set; } = 30;
    public int PoolWaitPct { get; set; } = 5;
    public int CpuStealPct { get; set; } = 5;
    public int CpuPct { get; set; } = 85;
    public int LoadPctOfCores { get; set; } = 150;
    public int MemAvailablePct { get; set; } = 10;
    public int DiskAwaitMs { get; set; } = 20;
    public int DiskUtilPct { get; set; } = 80;
    public int DiskFreePct { get; set; } = 10;
    public int ThreadPoolQueuePct { get; set; } = 20;
    public int GcPausePct { get; set; } = 5;
    public int HeapGrowthPct { get; set; } = 50;
    public int NetworkSharePct { get; set; } = 50;
    public int LcpMs { get; set; } = 2500;
    public int InpMs { get; set; } = 200;
    public int ClsMilli { get; set; } = 100;
    public int LongTaskMs { get; set; } = 200;
    public int LongTasksPerView { get; set; } = 5;
    public int ApiCallsPerScreen { get; set; } = 15;
    public int ResponseKb { get; set; } = 500;
    public int WorkerOverlapPct { get; set; } = 50;
    public int RegressionPct { get; set; } = 30;
    public int ExternalErrorPct { get; set; } = 5;

    public static async Task<FindingThresholds> LoadAsync(ISettingsService s, CancellationToken ct)
    {
        async Task<int> I(string key, int min, int max) => Math.Clamp(await s.GetAsync<int>(key, ct), min, max);
        return new FindingThresholds
        {
            RouteP95Ms = await I(SettingKeys.Performance.FindingRouteP95Ms, 10, 120_000),
            RouteMinRequests = await I(SettingKeys.Performance.FindingRouteMinRequests, 1, 1_000_000),
            DbSharePct = await I(SettingKeys.Performance.FindingDbSharePct, 1, 100),
            AppSharePct = await I(SettingKeys.Performance.FindingAppSharePct, 1, 100),
            ExtSharePct = await I(SettingKeys.Performance.FindingExtSharePct, 1, 100),
            NPlusOneMinRequests = await I(SettingKeys.Performance.FindingNPlusOneMinRequests, 1, 1_000_000),
            SlowQueryTopN = await I(SettingKeys.Performance.FindingSlowQueryTopN, 0, 50),
            SlowQueryMs = await I(SettingKeys.Performance.SlowQueryThresholdMs, 1, 60_000),
            SeqScanMinRows = await I(SettingKeys.Performance.FindingSeqScanMinRows, 100, int.MaxValue),
            SeqScanRatio = await I(SettingKeys.Performance.FindingSeqScanRatio, 1, 100_000),
            DeadTuplePct = await I(SettingKeys.Performance.FindingDeadTuplePct, 1, 1000),
            VacuumStaleDays = await I(SettingKeys.Performance.FindingVacuumStaleDays, 1, 365),
            CacheHitPct = await I(SettingKeys.Performance.FindingCacheHitPct, 50, 100),
            TempMb = await I(SettingKeys.Performance.FindingTempMb, 1, 1_000_000),
            IdleInTxSeconds = await I(SettingKeys.Performance.FindingIdleInTxSeconds, 1, 86_400),
            PoolWaitPct = await I(SettingKeys.Performance.FindingPoolWaitPct, 1, 100),
            CpuStealPct = await I(SettingKeys.Performance.FindingCpuStealPct, 1, 100),
            CpuPct = await I(SettingKeys.Performance.FindingCpuPct, 10, 100),
            LoadPctOfCores = await I(SettingKeys.Performance.FindingLoadPctOfCores, 50, 1000),
            MemAvailablePct = await I(SettingKeys.Performance.FindingMemAvailablePct, 1, 90),
            DiskAwaitMs = await I(SettingKeys.Performance.FindingDiskAwaitMs, 1, 10_000),
            DiskUtilPct = await I(SettingKeys.Performance.FindingDiskUtilPct, 10, 100),
            DiskFreePct = await I(SettingKeys.Performance.FindingDiskFreePct, 1, 90),
            ThreadPoolQueuePct = await I(SettingKeys.Performance.FindingThreadPoolQueuePct, 1, 100),
            GcPausePct = await I(SettingKeys.Performance.FindingGcPausePct, 1, 100),
            HeapGrowthPct = await I(SettingKeys.Performance.FindingHeapGrowthPct, 5, 10_000),
            NetworkSharePct = await I(SettingKeys.Performance.FindingNetworkSharePct, 5, 100),
            LcpMs = await I(SettingKeys.Performance.FindingLcpMs, 100, 60_000),
            InpMs = await I(SettingKeys.Performance.FindingInpMs, 10, 10_000),
            ClsMilli = await I(SettingKeys.Performance.FindingClsMilli, 1, 10_000),
            LongTaskMs = await I(SettingKeys.Performance.FindingLongTaskMs, 50, 10_000),
            LongTasksPerView = await I(SettingKeys.Performance.FindingLongTasksPerView, 1, 1000),
            ApiCallsPerScreen = await I(SettingKeys.Performance.FindingApiCallsPerScreen, 1, 1000),
            ResponseKb = await I(SettingKeys.Performance.FindingResponseKb, 10, 1_000_000),
            WorkerOverlapPct = await I(SettingKeys.Performance.FindingWorkerOverlapPct, 5, 10_000),
            RegressionPct = await I(SettingKeys.Performance.FindingRegressionPct, 5, 10_000),
            ExternalErrorPct = await I(SettingKeys.Performance.FindingExternalErrorPct, 1, 100),
        };
    }
}
