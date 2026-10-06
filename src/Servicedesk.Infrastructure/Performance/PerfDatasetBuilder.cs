using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

/// Gathers a <see cref="PerfDataset"/> for a period. Every section is
/// fetched defensively: a missing view (no pg_monitor), an unreadable
/// bundle manifest or an empty table yields an empty section, never a
/// failed dashboard — the report lists what was unavailable.
public sealed class PerfDatasetBuilder
{
    private readonly PerfQueries _queries;
    private readonly PgInspector _pg;
    private readonly IPerfSettings _perf;
    private readonly ISettingsService _settings;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PerfDatasetBuilder> _logger;

    public PerfDatasetBuilder(PerfQueries queries, PgInspector pg, IPerfSettings perf, ISettingsService settings,
        NpgsqlDataSource dataSource, ILogger<PerfDatasetBuilder> logger)
    {
        _queries = queries;
        _pg = pg;
        _perf = perf;
        _settings = settings;
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<PerfDataset> BuildAsync(DateTimeOffset from, DateTimeOffset to, bool includeLive, bool compare, CancellationToken ct)
    {
        var gaps = new List<string>();
        var period = await _queries.ResolveAsync(from, to, ct);
        var series = await _queries.ResolveSeriesAsync(from, to, ct);
        var thresholds = await FindingThresholds.LoadAsync(_settings, ct);
        var access = _pg.Access;
        if (!access.Checked && includeLive)
        {
            access = await Safe(gaps, "PostgreSQL access", () => _pg.CheckAccessAsync(ct), PgAccess.Unknown);
        }

        var routes = await Safe(gaps, "API routes", () => _queries.HttpRoutesAsync(period, ct), Array.Empty<HttpRouteStats>());
        var total = await Safe(gaps, "API totals", () => _queries.HttpTotalAsync(period, ct), null);
        var timeline = await Safe(gaps, "API timeline", () => _queries.HttpSeriesAsync(series, ct), Array.Empty<TimePoint>());
        var queries = await Safe(gaps, "app queries", () => _queries.DbQueriesAsync(period, 150, ct), Array.Empty<DbQueryStats>());
        var nPlusOne = await Safe(gaps, "N+1", () => _queries.NPlusOneAsync(period, ct), Array.Empty<NPlusOneStats>());
        var pgStatements = await Safe(gaps, "pg_stat_statements", () => _queries.PgStatementDeltasAsync(from, to, ct), Array.Empty<PgStatementDelta>());
        var pgDb = await Safe(gaps, "database statistics", () => _queries.PgDatabaseStatsAsync(from, to, ct), new DbPeriodStats());
        var tableDeltas = await Safe(gaps, "table statistics", () => _queries.TableDeltasAsync(from, to, ct), Array.Empty<TableDelta>());
        var runtime = await Safe(gaps, "runtime", () => _queries.RuntimeSummaryAsync(from, to, thresholds.CpuPct, thresholds.LoadPctOfCores, ct), new RuntimeSummary());
        var spans = await Safe(gaps, "spans", () => _queries.SpansAsync(period,
            new[] { "external", "hub", "hub-connection", "broadcast", "search", "db" }, ct), Array.Empty<SpanStats>());
        var rum = await Safe(gaps, "real-user metrics", () => _queries.RumAsync(period, ct), Array.Empty<RumStats>());
        var rumBreakdown = await Safe(gaps, "real-user breakdown", () => _queries.RumBreakdownAsync(period, ct), Array.Empty<RumBreakdown>());
        var workers = await Safe(gaps, "workers", () => _queries.WorkerSummaryAsync(from, to, ct), Array.Empty<WorkerSummary>());
        var workerRuns = await Safe(gaps, "worker runs", () => _queries.WorkerRunsAsync(from, to, 1500, ct), Array.Empty<WorkerRunRow>());
        var slow = await Safe(gaps, "slow requests", () => _queries.SlowRequestsAsync(from, to, 100, ct), Array.Empty<SlowRequestRow>());
        var markers = await Safe(gaps, "markers", () => _queries.MarkersAsync(from, to, ct), Array.Empty<MarkerRow>());
        var versions = await Safe(gaps, "versions", () => _queries.VersionsAsync(from.AddDays(-14), to, ct), Array.Empty<VersionRange>());

        IReadOnlyList<PgInspector.TableRow> tables = Array.Empty<PgInspector.TableRow>();
        IReadOnlyList<PgInspector.BloatRow> bloat = Array.Empty<PgInspector.BloatRow>();
        IReadOnlyList<PgInspector.IndexRow> indexes = Array.Empty<PgInspector.IndexRow>();
        IReadOnlyList<PgInspector.DuplicateIndexRow> duplicates = Array.Empty<PgInspector.DuplicateIndexRow>();
        IReadOnlyList<PgInspector.SettingRow> pgSettings = Array.Empty<PgInspector.SettingRow>();
        long storage = 0;
        if (includeLive)
        {
            tables = await Safe(gaps, "tables (live)", () => _pg.ReadTablesAsync(ct), tables);
            bloat = await Safe(gaps, "bloat estimate", () => _pg.ReadBloatAsync(ct), bloat);
            indexes = await Safe(gaps, "indexes", () => _pg.ReadIndexesAsync(ct), indexes);
            duplicates = await Safe(gaps, "duplicate indexes", () => _pg.ReadDuplicateIndexesAsync(ct), duplicates);
            pgSettings = await Safe(gaps, "PostgreSQL settings", () => _pg.ReadSettingsAsync(ct), pgSettings);
            storage = await Safe(gaps, "monitor storage", () => _queries.StorageBytesAsync(ct), 0L);
        }

        VersionComparisonResult? versionComparison = null;
        var recentVersions = versions.OrderByDescending(v => v.LastUtc).Take(2).ToList();
        if (recentVersions.Count == 2)
        {
            var after = recentVersions[0].Version;
            var before = recentVersions[1].Version;
            var cmp = await Safe(gaps, "version comparison", () => _queries.CompareVersionsAsync(before, after, ct), Array.Empty<RouteComparison>());
            versionComparison = new VersionComparisonResult(before, after, cmp);
        }

        IReadOnlyList<RouteComparison>? periodComparison = null;
        if (compare)
        {
            periodComparison = await Safe(gaps, "previous period", () => _queries.ComparePeriodsAsync(period, ct), Array.Empty<RouteComparison>());
        }

        var budgets = Budget.Parse(await _settings.GetAsync<string>(SettingKeys.Performance.Budgets, ct));
        foreach (var b in budgets)
        {
            var r = routes.FirstOrDefault(x => x.Method == b.Method && x.Route == b.Route);
            if (r is null) continue;
            b.Count = r.Count;
            b.P95 = r.P95;
        }

        var overlaps = Array.Empty<WorkerOverlap>() as IReadOnlyList<WorkerOverlap>;
        if (to - from <= TimeSpan.FromDays(2) && workerRuns.Count > 0)
        {
            var minutes = await Safe(gaps, "minute totals", () => _queries.HttpMinuteTotalsAsync(from, to, ct), Array.Empty<TimePoint>());
            overlaps = ComputeOverlaps(minutes, workerRuns);
        }

        return new PerfDataset
        {
            Period = period,
            GeneratedUtc = _perf.Time.GetUtcNow(),
            Options = _perf.Options,
            Level = _perf.Level,
            Thresholds = thresholds,
            Total = total,
            Routes = routes,
            Timeline = timeline,
            Queries = queries,
            NPlusOne = nPlusOne,
            PgStatements = pgStatements,
            PgDatabase = pgDb,
            TableDeltas = tableDeltas,
            Tables = tables,
            Bloat = bloat,
            Indexes = indexes,
            DuplicateIndexes = duplicates,
            PgSettings = pgSettings,
            Runtime = runtime,
            Spans = spans,
            Rum = rum,
            RumBreakdown = rumBreakdown,
            Workers = workers,
            WorkerRuns = workerRuns,
            WorkerOverlaps = overlaps,
            SlowRequests = slow,
            Markers = markers,
            Versions = versions,
            VersionComparison = versionComparison,
            PeriodComparison = periodComparison,
            Budgets = budgets,
            PgAccess = access,
            HostSupported = HostMetricsReader.IsSupported,
            MonitorStorageBytes = storage,
            Bundle = ReadBundle(),
            MaxPoolSize = SafePoolSize(),
            Gaps = gaps,
        };
    }

    /// API p95 in minutes overlapping each worker's runs vs all other minutes.
    public static IReadOnlyList<WorkerOverlap> ComputeOverlaps(IReadOnlyList<TimePoint> minutes, IReadOnlyList<WorkerRunRow> runs)
    {
        if (minutes.Count == 0) return Array.Empty<WorkerOverlap>();
        var result = new List<WorkerOverlap>();
        foreach (var g in runs.Where(r => r.DurationMs >= 1000).GroupBy(r => r.Worker))
        {
            var covered = new HashSet<DateTime>();
            foreach (var run in g)
            {
                var start = Floor(run.StartedUtc);
                var end = run.StartedUtc.AddMilliseconds(run.DurationMs);
                for (var m = start; m <= end; m = m.AddMinutes(1)) covered.Add(m);
            }
            var during = minutes.Where(p => covered.Contains(Floor(p.T))).ToList();
            var outside = minutes.Where(p => !covered.Contains(Floor(p.T))).ToList();
            if (during.Count == 0 || outside.Count == 0) continue;
            var duringHist = PerfHistogram.Merge(during.Select(p => (IReadOnlyList<long>)p.Hist));
            var outsideHist = PerfHistogram.Merge(outside.Select(p => (IReadOnlyList<long>)p.Hist));
            result.Add(new WorkerOverlap(
                g.Key,
                during.Count,
                during.Sum(p => p.Count),
                PerfHistogram.Percentile(duringHist, 95, during.Max(p => p.MaxMs)),
                PerfHistogram.Percentile(outsideHist, 95, outside.Max(p => p.MaxMs))));
        }
        return result;

        static DateTime Floor(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
    }

    public static BundleInfo? ReadBundle()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "perf-bundle.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BundleInfo>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private int SafePoolSize()
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString).MaxPoolSize;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<T> Safe<T>(List<string> gaps, string label, Func<Task<T>> fetch, T fallback)
    {
        try
        {
            return await fetch();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Performance dataset section '{Section}' unavailable.", label);
            gaps.Add(label);
            return fallback;
        }
    }
}
