using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

/// Read side of the monitor. Aggregation happens in SQL (sums, maxima and the
/// element-wise perf_hist_sum over histograms); percentiles are computed from
/// the merged histograms in C#. Table names in the dynamic FROM clauses are
/// compile-time constants — nothing a caller passes ever becomes SQL text.
public sealed class PerfQueries
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public PerfQueries(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    // ------------------------------------------------------------ periods

    public async Task<PerfPeriod> ResolveAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (to <= from) to = from.AddMinutes(1);
        var span = to - from;
        var step = StepFor(span);
        if (span <= TimeSpan.FromHours(6))
        {
            return new PerfPeriod(from, to, false, Epoch, Epoch, from.UtcDateTime, to.UtcDateTime, step);
        }

        var rolledUntil = await RolledUntilAsync(ct);
        var hourFrom = FloorHour(from.UtcDateTime);
        var hourTo = rolledUntil is null ? hourFrom : Min(rolledUntil.Value, to.UtcDateTime);
        if (hourTo < hourFrom) hourTo = hourFrom;
        var minuteFrom = Max(from.UtcDateTime, hourTo);
        return new PerfPeriod(from, to, true, hourFrom, hourTo, minuteFrom, to.UtcDateTime, step);
    }

    /// Time series prefer the minute tables (full resolution) while they still
    /// hold the whole period; beyond minute retention the hourly rows are used.
    public async Task<PerfPeriod> ResolveSeriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (to <= from) to = from.AddMinutes(1);
        var minuteDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.RetentionMinuteDays, ct), 1, 90);
        var step = StepFor(to - from);
        if (from >= DateTimeOffset.UtcNow.AddDays(-minuteDays).AddHours(1))
        {
            return new PerfPeriod(from, to, false, Epoch, Epoch, from.UtcDateTime, to.UtcDateTime, step);
        }
        var resolved = await ResolveAsync(from, to, ct);
        return resolved with { StepSeconds = Math.Max(step, 3600) };
    }

    public static int StepFor(TimeSpan span) => span.TotalHours switch
    {
        <= 1 => 60,
        <= 6 => 300,
        <= 24 => 900,
        <= 72 => 3600,
        <= 24 * 14 => 3 * 3600,
        _ => 24 * 3600,
    };

    private async Task<DateTime?> RolledUntilAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT value_utc FROM perf_rollup_state WHERE name = 'hour'", cancellationToken: ct));
    }

    private static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// UNION ALL of the hourly and minute twin over the resolved period.
    private static string Source(string baseTable, string columns) => $"""
        SELECT {columns} FROM {baseTable}_hour WHERE bucket_utc >= @HourFrom AND bucket_utc < @HourTo
        UNION ALL
        SELECT {columns} FROM {baseTable}_minute WHERE bucket_utc >= @MinuteFrom AND bucket_utc < @MinuteTo
        """;

    private static object Params(PerfPeriod p, object? extra = null)
    {
        var d = new DynamicParameters(new
        {
            p.HourFrom,
            p.HourTo,
            p.MinuteFrom,
            p.MinuteTo,
            From = p.From.UtcDateTime,
            To = p.To.UtcDateTime,
            Step = p.StepSeconds,
        });
        if (extra is not null) d.AddDynamicParams(extra);
        return d;
    }

    // ---------------------------------------------------------------- HTTP

    private const string HttpColumns =
        "bucket_utc, method, route, status_class, count, err_count, sum_ms, max_ms, hist, db_count, db_ms, ext_ms, pipeline_ms, bytes_sum, size_hist, users";

    public async Task<IReadOnlyList<HttpRouteStats>> HttpRoutesAsync(PerfPeriod p, CancellationToken ct, string? method = null, string? route = null)
    {
        var sql = $"""
            SELECT method AS Method, route AS Route,
                   sum(count)::bigint AS Count,
                   sum(err_count)::bigint AS Errors,
                   COALESCE(sum(count) FILTER (WHERE status_class = '4xx'), 0)::bigint AS Count4xx,
                   COALESCE(sum(count) FILTER (WHERE status_class = '429'), 0)::bigint AS Count429,
                   sum(sum_ms) AS SumMs, max(max_ms) AS MaxMs, perf_hist_sum(hist) AS Hist,
                   sum(db_count)::bigint AS DbCount, sum(db_ms) AS DbMs, sum(ext_ms) AS ExtMs,
                   sum(pipeline_ms) AS PipelineMs, sum(bytes_sum)::bigint AS Bytes,
                   perf_hist_sum(size_hist) AS SizeHist, max(users) AS Users
            FROM ({Source("perf_http", HttpColumns)}) s
            WHERE method <> '*'
              AND (@Method::text IS NULL OR method = @Method)
              AND (@Route::text IS NULL OR route = @Route)
            GROUP BY method, route
            ORDER BY sum(sum_ms) DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<HttpRouteStats>(new CommandDefinition(sql,
            Params(p, new { Method = method, Route = route }), cancellationToken: ct));
        return rows.AsList();
    }

    /// API totals over time ('*' rows), or one route's series.
    public async Task<IReadOnlyList<TimePoint>> HttpSeriesAsync(PerfPeriod p, CancellationToken ct, string? method = null, string? route = null)
    {
        var filter = method is null ? "method = '*' AND route = '*'" : "method = @Method AND route = @Route";
        var sql = $"""
            SELECT date_bin(make_interval(secs => @Step), bucket_utc, TIMESTAMPTZ '2000-01-01') AS T,
                   sum(count)::bigint AS Count, sum(err_count)::bigint AS Errors, sum(sum_ms) AS SumMs,
                   max(max_ms) AS MaxMs, perf_hist_sum(hist) AS Hist, sum(db_ms) AS DbMs, sum(ext_ms) AS ExtMs,
                   max(users) AS Users
            FROM ({Source("perf_http", HttpColumns)}) s
            WHERE {filter}
            GROUP BY 1
            ORDER BY 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimePoint>(new CommandDefinition(sql,
            Params(p, new { Method = method, Route = route }), cancellationToken: ct));
        return rows.AsList();
    }

    /// Per-minute API totals ('*' rows) — used for the worker-overlap analysis.
    public async Task<IReadOnlyList<TimePoint>> HttpMinuteTotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimePoint>(new CommandDefinition("""
            SELECT bucket_utc AS T, count AS Count, err_count AS Errors, sum_ms AS SumMs, max_ms AS MaxMs, hist AS Hist,
                   db_ms AS DbMs, ext_ms AS ExtMs, users AS Users
            FROM perf_http_minute
            WHERE method = '*' AND route = '*' AND bucket_utc >= @From AND bucket_utc < @To
            ORDER BY bucket_utc
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<HttpRouteStats?> HttpTotalAsync(PerfPeriod p, CancellationToken ct)
    {
        var sql = $"""
            SELECT '*' AS Method, '*' AS Route, COALESCE(sum(count), 0)::bigint AS Count,
                   COALESCE(sum(err_count), 0)::bigint AS Errors,
                   0::bigint AS Count4xx, 0::bigint AS Count429,
                   COALESCE(sum(sum_ms), 0) AS SumMs, COALESCE(max(max_ms), 0) AS MaxMs,
                   COALESCE(perf_hist_sum(hist), ARRAY[]::bigint[]) AS Hist,
                   COALESCE(sum(db_count), 0)::bigint AS DbCount, COALESCE(sum(db_ms), 0) AS DbMs,
                   COALESCE(sum(ext_ms), 0) AS ExtMs, COALESCE(sum(pipeline_ms), 0) AS PipelineMs,
                   COALESCE(sum(bytes_sum), 0)::bigint AS Bytes,
                   COALESCE(perf_hist_sum(size_hist), ARRAY[]::bigint[]) AS SizeHist,
                   COALESCE(max(users), 0) AS Users
            FROM ({Source("perf_http", HttpColumns)}) s
            WHERE method = '*' AND route = '*'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<HttpRouteStats>(new CommandDefinition(sql, Params(p), cancellationToken: ct));
        if (row is not null)
        {
            // 4xx / 429 across all API routes come from the per-route rows.
            var counts = await conn.QuerySingleAsync<StatusCounts>(new CommandDefinition($"""
                SELECT COALESCE(sum(count) FILTER (WHERE status_class = '4xx'), 0)::bigint AS C4,
                       COALESCE(sum(count) FILTER (WHERE status_class = '429'), 0)::bigint AS C429
                FROM ({Source("perf_http", HttpColumns)}) s
                WHERE method <> '*' AND route LIKE '/api/%'
                """, Params(p), cancellationToken: ct));
            row.Count4xx = counts.C4;
            row.Count429 = counts.C429;
        }
        return row;
    }

    // ------------------------------------------------------------ DB (app)

    private const string DbColumns = "bucket_utc, fingerprint, source, count, err_count, sum_ms, max_ms, hist";

    public async Task<IReadOnlyList<DbQueryStats>> DbQueriesAsync(PerfPeriod p, int limit, CancellationToken ct, string? source = null)
    {
        var sql = $"""
            SELECT q.fingerprint AS Fingerprint, COALESCE(f.sql_text, '') AS Sql, f.caller AS Caller,
                   q.Count, q.Errors, q.SumMs, q.MaxMs, q.Hist
            FROM (
                SELECT fingerprint, sum(count)::bigint AS Count, sum(err_count)::bigint AS Errors,
                       sum(sum_ms) AS SumMs, max(max_ms) AS MaxMs, perf_hist_sum(hist) AS Hist
                FROM ({Source("perf_db_query", DbColumns)}) s
                WHERE (@Source::text IS NULL OR source = @Source)
                GROUP BY fingerprint
                ORDER BY sum(sum_ms) DESC
                LIMIT @Limit
            ) q
            LEFT JOIN perf_sql_fingerprint f ON f.fingerprint = q.fingerprint
            ORDER BY q.SumMs DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<DbQueryStats>(new CommandDefinition(sql,
            Params(p, new { Limit = limit, Source = source }), cancellationToken: ct))).AsList();
        if (rows.Count == 0) return rows;

        var shares = await conn.QueryAsync<SourceShare>(new CommandDefinition($"""
            SELECT fingerprint AS Fingerprint, source AS Source, sum(count)::bigint AS Count, sum(sum_ms) AS SumMs
            FROM ({Source("perf_db_query", DbColumns)}) s
            WHERE fingerprint = ANY(@Fingerprints)
            GROUP BY fingerprint, source
            """, Params(p, new { Fingerprints = rows.Select(r => r.Fingerprint).ToArray() }), cancellationToken: ct));
        var byFp = shares.GroupBy(s => s.Fingerprint).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.SumMs).Take(5).ToList());
        foreach (var r in rows)
        {
            if (byFp.TryGetValue(r.Fingerprint, out var list)) r.Sources = list;
            r.Sql = PerfRedactor.Sql(r.Sql);
        }
        return rows;
    }

    public async Task<IReadOnlyList<NPlusOneStats>> NPlusOneAsync(PerfPeriod p, CancellationToken ct, string? source = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<NPlusOneStats>(new CommandDefinition("""
            SELECT n.route AS Route, n.fingerprint AS Fingerprint, f.sql_text AS Sql, f.caller AS Caller,
                   sum(n.requests)::bigint AS Requests, sum(n.executions)::bigint AS Executions,
                   max(n.max_per_request)::bigint AS MaxPerRequest
            FROM perf_nplusone_minute n
            LEFT JOIN perf_sql_fingerprint f ON f.fingerprint = n.fingerprint
            WHERE n.bucket_utc >= @From AND n.bucket_utc < @To
              AND (@Source::text IS NULL OR n.route = @Source)
            GROUP BY n.route, n.fingerprint, f.sql_text, f.caller
            ORDER BY sum(n.executions) DESC
            LIMIT 100
            """, new { From = p.From.UtcDateTime, To = p.To.UtcDateTime, Source = source }, cancellationToken: ct))).AsList();
        foreach (var r in rows) r.Sql = r.Sql is null ? null : PerfRedactor.Sql(r.Sql);
        return rows;
    }

    // -------------------------------------------------------- spans / RUM

    private const string SpanColumns = "bucket_utc, kind, name, detail, count, err_count, sum_ms, max_ms, hist";

    public async Task<IReadOnlyList<SpanStats>> SpansAsync(PerfPeriod p, string[] kinds, CancellationToken ct)
    {
        var sql = $"""
            SELECT kind AS Kind, name AS Name, detail AS Detail, sum(count)::bigint AS Count,
                   sum(err_count)::bigint AS Errors, sum(sum_ms) AS SumMs, max(max_ms) AS MaxMs,
                   perf_hist_sum(hist) AS Hist
            FROM ({Source("perf_span", SpanColumns)}) s
            WHERE kind = ANY(@Kinds)
            GROUP BY kind, name, detail
            ORDER BY sum(sum_ms) DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SpanStats>(new CommandDefinition(sql, Params(p, new { Kinds = kinds }), cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<SeriesValue>> SpanCountSeriesAsync(PerfPeriod p, string kind, CancellationToken ct)
    {
        var sql = $"""
            SELECT date_bin(make_interval(secs => @Step), bucket_utc, TIMESTAMPTZ '2000-01-01') AS T,
                   sum(count)::float8 AS Value
            FROM ({Source("perf_span", SpanColumns)}) s
            WHERE kind = @Kind
            GROUP BY 1 ORDER BY 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SeriesValue>(new CommandDefinition(sql, Params(p, new { Kind = kind }), cancellationToken: ct));
        return rows.AsList();
    }

    private const string RumColumns = "bucket_utc, route, metric, detail, device, connection, count, sum_value, max_value, hist";

    public async Task<IReadOnlyList<RumStats>> RumAsync(PerfPeriod p, CancellationToken ct)
    {
        var sql = $"""
            SELECT route AS Route, metric AS Metric, detail AS Detail, sum(count)::bigint AS Count,
                   sum(sum_value) AS SumValue, max(max_value) AS MaxValue, perf_hist_sum(hist) AS Hist
            FROM ({Source("perf_rum", RumColumns)}) s
            GROUP BY route, metric, detail
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RumStats>(new CommandDefinition(sql, Params(p), cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<RumBreakdown>> RumBreakdownAsync(PerfPeriod p, CancellationToken ct)
    {
        var sql = $"""
            SELECT 'device' AS Dimension, device AS Value, sum(count)::bigint AS Count
            FROM ({Source("perf_rum", RumColumns)}) s WHERE metric = 'view' GROUP BY device
            UNION ALL
            SELECT 'connection', connection, sum(count)::bigint
            FROM ({Source("perf_rum", RumColumns)}) s WHERE metric = 'view' GROUP BY connection
            UNION ALL
            SELECT 'protocol', detail, sum(count)::bigint
            FROM ({Source("perf_rum", RumColumns)}) s WHERE metric = 'protocol' GROUP BY detail
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RumBreakdown>(new CommandDefinition(sql, Params(p), cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<SeriesValue>> RumSeriesAsync(PerfPeriod p, string metric, CancellationToken ct)
    {
        var sql = $"""
            SELECT date_bin(make_interval(secs => @Step), bucket_utc, TIMESTAMPTZ '2000-01-01') AS T,
                   CASE WHEN sum(count) > 0 THEN sum(sum_value) / sum(count) ELSE 0 END AS Value
            FROM ({Source("perf_rum", RumColumns)}) s
            WHERE metric = @Metric
            GROUP BY 1 ORDER BY 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SeriesValue>(new CommandDefinition(sql, Params(p, new { Metric = metric }), cancellationToken: ct));
        return rows.AsList();
    }

    // ------------------------------------------------------------- runtime

    public async Task<RuntimeSummary> RuntimeSummaryAsync(DateTimeOffset from, DateTimeOffset to,
        int cpuHighPct, int loadHighPctOfCores, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var summary = await conn.QuerySingleAsync<RuntimeSummary>(new CommandDefinition("""
            SELECT count(*) AS Minutes,
                   count(*) FILTER (WHERE level = 'diagnose') AS DiagnoseMinutes,
                   COALESCE(sum(requests), 0)::bigint AS Requests,
                   COALESCE(sum(overhead_ms), 0) AS OverheadMs,
                   COALESCE(max(active_users), 0) AS ActiveUsersPeak,
                   avg(cpu_pct) AS CpuPct, max(cpu_pct_max) AS CpuPctMax,
                   avg(working_set_mb) AS WorkingSetMb, max(gc_heap_mb_max) AS GcHeapMbMax,
                   COALESCE(sum(gc_pause_ms), 0) AS GcPauseMs, COALESCE(sum(interval_ms), 0) AS IntervalMs,
                   COALESCE(sum(gen2), 0)::bigint AS Gen2,
                   COALESCE(sum(samples), 0)::bigint AS Samples,
                   COALESCE(sum(tp_queue_samples), 0)::bigint AS TpQueueSamples,
                   COALESCE(max(tp_threads_max), 0) AS TpThreadsMax,
                   COALESCE(sum(exceptions), 0)::bigint AS Exceptions,
                   COALESCE(sum(lock_contentions), 0)::bigint AS LockContentions,
                   COALESCE(sum(pool_wait_samples), 0)::bigint AS PoolWaitSamples,
                   COALESCE(max(pool_pending_max), 0)::bigint AS PoolPendingMax,
                   COALESCE(sum(pool_timeouts), 0)::bigint AS PoolTimeouts,
                   COALESCE(max(pool_max), 0)::bigint AS PoolMax,
                   COALESCE(max(pool_busy_max), 0)::bigint AS PoolBusyMax,
                   COALESCE(max(in_flight_max), 0)::bigint AS InFlightMax,
                   COALESCE(max(kestrel_queued_max), 0)::bigint AS KestrelQueuedMax,
                   COALESCE(max(signalr_conn_max), 0)::bigint AS SignalRConnMax,
                   avg(host_cpu_pct) AS HostCpuPct, max(host_cpu_max) AS HostCpuMax,
                   count(*) FILTER (WHERE host_cpu_pct > @CpuHigh) AS HostCpuHighMinutes,
                   count(host_cpu_pct) AS HostMinutes,
                   avg(host_steal_pct) AS StealPct, max(host_steal_pct) AS StealMax,
                   avg(host_iowait_pct) AS IoWaitPct,
                   avg(load1) AS Load1, max(load1) AS Load1Max,
                   count(*) FILTER (WHERE host_cores > 0 AND load1 > host_cores * @LoadHigh / 100.0) AS LoadHighMinutes,
                   max(host_cores) AS HostCores,
                   max(mem_total_mb) AS MemTotalMb, min(mem_available_mb) AS MemAvailableMinMb,
                   max(swap_used_mb) AS SwapUsedMaxMb,
                   sum(COALESCE(swap_in, 0) + COALESCE(swap_out, 0)) AS SwapActivity,
                   avg(disk_await_ms) AS DiskAwaitMs, max(disk_await_ms) AS DiskAwaitMax,
                   max(disk_util_pct) AS DiskUtilMax, avg(disk_util_pct) AS DiskUtilAvg,
                   avg(cg_throttled_pct) AS CgThrottledPct, max(cg_mem_limit_mb) AS CgMemLimitMb,
                   max(oom_kills) - min(oom_kills) AS OomKillsDelta,
                   min(root_free_pct) AS RootFreePct, min(blob_free_pct) AS BlobFreePct,
                   min(root_free_gb) AS RootFreeGb, min(blob_free_gb) AS BlobFreeGb
            FROM perf_runtime_minute
            WHERE bucket_utc >= @From AND bucket_utc < @To
            """, new { From = from.UtcDateTime, To = to.UtcDateTime, CpuHigh = cpuHighPct, LoadHigh = loadHighPctOfCores },
            cancellationToken: ct));

        // Heap and thread-count trend: average of the first vs the last tenth
        // of the period (single samples are too noisy for a leak signal).
        var trend = await conn.QuerySingleAsync(new CommandDefinition("""
            WITH r AS (
                SELECT gc_heap_mb, tp_threads_avg,
                       ntile(10) OVER (ORDER BY bucket_utc) AS tile
                FROM perf_runtime_minute
                WHERE bucket_utc >= @From AND bucket_utc < @To
            )
            SELECT avg(gc_heap_mb) FILTER (WHERE tile = 1) AS heap_start,
                   avg(gc_heap_mb) FILTER (WHERE tile = 10) AS heap_end,
                   avg(tp_threads_avg) FILTER (WHERE tile = 1) AS threads_start,
                   avg(tp_threads_avg) FILTER (WHERE tile = 10) AS threads_end
            FROM r
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        var map = (IDictionary<string, object?>)trend;
        summary.GcHeapMbStart = map["heap_start"] as double?;
        summary.GcHeapMbEnd = map["heap_end"] as double?;
        summary.TpThreadsStart = map["threads_start"] as double?;
        summary.TpThreadsEnd = map["threads_end"] as double?;
        return summary;
    }

    public async Task<IReadOnlyList<RuntimePoint>> RuntimeSeriesAsync(DateTimeOffset from, DateTimeOffset to, int stepSeconds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RuntimePoint>(new CommandDefinition("""
            SELECT date_bin(make_interval(secs => @Step), bucket_utc, TIMESTAMPTZ '2000-01-01') AS T,
                   avg(cpu_pct) AS CpuPct, avg(host_cpu_pct) AS HostCpuPct, avg(host_steal_pct) AS StealPct,
                   avg(host_iowait_pct) AS IoWaitPct, avg(load1) AS Load1,
                   avg(mem_available_mb) AS MemAvailableMb, max(mem_total_mb) AS MemTotalMb,
                   max(swap_used_mb) AS SwapUsedMb, avg(disk_await_ms) AS DiskAwaitMs, max(disk_util_pct) AS DiskUtilPct,
                   avg(working_set_mb) AS WorkingSetMb, avg(gc_heap_mb) AS GcHeapMb,
                   CASE WHEN sum(interval_ms) > 0 THEN 100 * sum(gc_pause_ms) / sum(interval_ms) END AS GcPausePct,
                   avg(tp_threads_avg) AS TpThreads, max(tp_queue_max)::float8 AS TpQueue,
                   avg(pool_busy_avg) AS PoolBusy, max(pool_max)::float8 AS PoolMax,
                   max(pool_pending_max)::float8 AS PoolPending, max(kestrel_active_max)::float8 AS KestrelActive,
                   max(signalr_conn_max)::float8 AS SignalR, sum(exceptions)::float8 AS Exceptions,
                   max(in_flight_max)::float8 AS InFlight, sum(requests)::float8 AS Requests,
                   sum(overhead_ms) AS OverheadMs
            FROM perf_runtime_minute
            WHERE bucket_utc >= @From AND bucket_utc < @To
            GROUP BY 1 ORDER BY 1
            """, new { From = from.UtcDateTime, To = to.UtcDateTime, Step = stepSeconds }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<VersionRange>> VersionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VersionRange>(new CommandDefinition("""
            SELECT app_version AS Version, min(bucket_utc) AS FirstUtc, max(bucket_utc) AS LastUtc, count(*) AS Minutes
            FROM perf_runtime_minute
            WHERE bucket_utc >= @From AND bucket_utc < @To
            GROUP BY app_version
            ORDER BY min(bucket_utc)
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        return rows.AsList();
    }

    /// p95 per route for two versions, from minute data joined on the version
    /// in force each minute (only the minute tables carry that link).
    public async Task<IReadOnlyList<RouteComparison>> CompareVersionsAsync(string before, string after, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VersionRouteRow>(new CommandDefinition("""
            SELECT r.app_version AS Version, h.method AS Method, h.route AS Route, sum(h.count)::bigint AS Count,
                   max(h.max_ms) AS MaxMs, perf_hist_sum(h.hist) AS Hist
            FROM perf_http_minute h
            JOIN perf_runtime_minute r ON r.bucket_utc = h.bucket_utc
            WHERE r.app_version = ANY(@Versions) AND h.method <> '*' AND h.route LIKE '/api/%'
            GROUP BY r.app_version, h.method, h.route
            """, new { Versions = new[] { before, after } }, cancellationToken: ct));
        return Compare(rows.Select(r => (r.Version == after, r.Method, r.Route, r.Count, r.MaxMs, r.Hist)));
    }

    /// p95 per route in this period vs the period of equal length before it.
    public async Task<IReadOnlyList<RouteComparison>> ComparePeriodsAsync(PerfPeriod current, CancellationToken ct)
    {
        var previous = await ResolveAsync(current.From - current.Span, current.From, ct);
        var before = await HttpRoutesAsync(previous, ct);
        var after = await HttpRoutesAsync(current, ct);
        return Compare(before.Select(b => (false, b.Method, b.Route, b.Count, b.MaxMs, b.Hist))
            .Concat(after.Select(a => (true, a.Method, a.Route, a.Count, a.MaxMs, a.Hist))));
    }

    internal static IReadOnlyList<RouteComparison> Compare(
        IEnumerable<(bool IsAfter, string Method, string Route, long Count, double MaxMs, long[] Hist)> rows)
    {
        var map = new Dictionary<string, RouteComparison>();
        foreach (var r in rows)
        {
            var key = r.Method + " " + r.Route;
            if (!map.TryGetValue(key, out var c)) map[key] = c = new RouteComparison { Method = r.Method, Route = r.Route };
            var p95 = PerfHistogram.Percentile(r.Hist, 95, r.MaxMs);
            if (r.IsAfter) { c.AfterCount = r.Count; c.AfterP95 = p95; }
            else { c.BeforeCount = r.Count; c.BeforeP95 = p95; }
        }
        return map.Values.Where(c => c.BeforeCount > 0 && c.AfterCount > 0).OrderByDescending(c => c.ChangePct).ToList();
    }

    // ---------------------------------------------------- events / markers

    public async Task<IReadOnlyList<WorkerRunRow>> WorkerRunsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<WorkerRunRow>(new CommandDefinition("""
            SELECT started_utc AS StartedUtc, worker AS Worker, duration_ms AS DurationMs, success AS Success,
                   items AS Items, error_kind AS ErrorKind
            FROM perf_worker_run
            WHERE started_utc >= @From AND started_utc < @To
            ORDER BY duration_ms DESC
            LIMIT @Limit
            """, new { From = from.UtcDateTime, To = to.UtcDateTime, Limit = limit }, cancellationToken: ct));
        return rows.OrderBy(r => r.StartedUtc).ToList();
    }

    public async Task<IReadOnlyList<WorkerSummary>> WorkerSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<WorkerSummary>(new CommandDefinition("""
            SELECT worker AS Worker, count(*) AS Runs, count(*) FILTER (WHERE NOT success) AS Failures,
                   avg(duration_ms) AS AvgMs, max(duration_ms) AS MaxMs, sum(duration_ms) AS TotalMs,
                   sum(items)::bigint AS Items, max(started_utc) AS LastRunUtc
            FROM perf_worker_run
            WHERE started_utc >= @From AND started_utc < @To
            GROUP BY worker
            ORDER BY sum(duration_ms) DESC
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<SlowRequestRow>> SlowRequestsAsync(DateTimeOffset from, DateTimeOffset to, int limit,
        CancellationToken ct, string? method = null, string? route = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SlowRequestRow>(new CommandDefinition("""
            SELECT id AS Id, ts_utc AS TsUtc, method AS Method, route AS Route, status AS Status, total_ms AS TotalMs,
                   db_ms AS DbMs, db_count AS DbCount, ext_ms AS ExtMs, pipeline_ms AS PipelineMs, gc_count AS GcCount,
                   bytes AS Bytes, breakdown::text AS Breakdown, trace_id AS TraceId
            FROM perf_slow_request
            WHERE ts_utc >= @From AND ts_utc < @To
              AND (@Method::text IS NULL OR method = @Method)
              AND (@Route::text IS NULL OR route = @Route)
            ORDER BY total_ms DESC
            LIMIT @Limit
            """, new { From = from.UtcDateTime, To = to.UtcDateTime, Limit = limit, Method = method, Route = route },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<MarkerRow>> MarkersAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MarkerRow>(new CommandDefinition("""
            SELECT id AS Id, ts_utc AS TsUtc, kind AS Kind, label AS Label, created_by AS CreatedBy
            FROM perf_marker
            WHERE ts_utc >= @From AND ts_utc < @To
            ORDER BY ts_utc
            LIMIT 500
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<long> AddMarkerAsync(string kind, string label, string? createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO perf_marker (kind, label, created_by) VALUES (@Kind, @Label, @CreatedBy) RETURNING id
            """, new { Kind = kind, Label = label, CreatedBy = createdBy }, cancellationToken: ct));
    }

    public async Task<bool> DeleteMarkerAsync(long id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM perf_marker WHERE id = @Id AND kind = 'manual'", new { Id = id }, cancellationToken: ct)) > 0;
    }

    // -------------------------------------------------------- pg snapshots

    public async Task<IReadOnlyList<PgStatementDelta>> PgStatementDeltasAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var bounds = await conn.QuerySingleAsync<Bounds>(new CommandDefinition("""
            SELECT COALESCE((SELECT max(ts_utc) FROM perf_pg_statement_snapshot WHERE ts_utc <= @From),
                            (SELECT min(ts_utc) FROM perf_pg_statement_snapshot WHERE ts_utc > @From AND ts_utc <= @To)) AS Start,
                   (SELECT max(ts_utc) FROM perf_pg_statement_snapshot WHERE ts_utc <= @To) AS End
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        if (bounds.End is null) return Array.Empty<PgStatementDelta>();

        const string snapshotSql = """
            SELECT ts_utc AS TsUtc, queryid AS QueryId, calls AS Calls, total_ms AS TotalMs, rows AS Rows,
                   blks_hit AS BlksHit, blks_read AS BlksRead, temp_written AS TempWritten,
                   stddev_ms AS StddevMs, max_ms AS MaxMs
            FROM perf_pg_statement_snapshot WHERE ts_utc = @Ts
            """;
        var end = (await conn.QueryAsync<PgStatementSnapshotRow>(new CommandDefinition(snapshotSql,
            new { Ts = bounds.End.Value }, cancellationToken: ct))).AsList();
        var start = bounds.Start is null || bounds.Start == bounds.End
            ? new List<PgStatementSnapshotRow>()
            : (await conn.QueryAsync<PgStatementSnapshotRow>(new CommandDefinition(snapshotSql,
                new { Ts = bounds.Start.Value }, cancellationToken: ct))).AsList();

        var startIds = start.Select(s => s.QueryId).ToHashSet();
        var missing = end.Where(e => !startIds.Contains(e.QueryId)).Select(e => e.QueryId).ToArray();
        var firstSeen = new List<PgStatementSnapshotRow>();
        if (start.Count > 0 && missing.Length > 0)
        {
            firstSeen = (await conn.QueryAsync<PgStatementSnapshotRow>(new CommandDefinition("""
                SELECT DISTINCT ON (queryid) ts_utc AS TsUtc, queryid AS QueryId, calls AS Calls, total_ms AS TotalMs,
                       rows AS Rows, blks_hit AS BlksHit, blks_read AS BlksRead, temp_written AS TempWritten,
                       stddev_ms AS StddevMs, max_ms AS MaxMs
                FROM perf_pg_statement_snapshot
                WHERE ts_utc > @Start AND ts_utc < @End AND queryid = ANY(@Ids)
                ORDER BY queryid, ts_utc
                """, new { Start = bounds.Start!.Value, End = bounds.End.Value, Ids = missing }, cancellationToken: ct))).AsList();
        }

        var deltas = PgDelta.Compute(start, end, firstSeen, cumulativeWhenNoStart: start.Count == 0);
        var texts = (await conn.QueryAsync<QueryTextRow>(new CommandDefinition(
            "SELECT queryid AS Id, query AS Query FROM perf_pg_query_text WHERE queryid = ANY(@Ids)",
            new { Ids = deltas.Select(d => d.QueryId).ToArray() }, cancellationToken: ct))).ToDictionary(t => t.Id, t => t.Query);
        foreach (var d in deltas) d.Query = texts.GetValueOrDefault(d.QueryId, "");
        return deltas;
    }

    public async Task<DbPeriodStats> PgDatabaseStatsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var startRow = await conn.QuerySingleOrDefaultAsync<SnapshotPayload>(new CommandDefinition("""
            SELECT ts_utc AS Ts, payload::text AS Payload FROM perf_pg_db_snapshot
            WHERE ts_utc <= @From ORDER BY ts_utc DESC LIMIT 1
            """, new { From = from.UtcDateTime }, cancellationToken: ct))
            ?? await conn.QuerySingleOrDefaultAsync<SnapshotPayload>(new CommandDefinition("""
            SELECT ts_utc AS Ts, payload::text AS Payload FROM perf_pg_db_snapshot
            WHERE ts_utc > @From AND ts_utc <= @To ORDER BY ts_utc LIMIT 1
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        var endRow = await conn.QuerySingleOrDefaultAsync<SnapshotPayload>(new CommandDefinition("""
            SELECT ts_utc AS Ts, payload::text AS Payload FROM perf_pg_db_snapshot
            WHERE ts_utc <= @To ORDER BY ts_utc DESC LIMIT 1
            """, new { To = to.UtcDateTime }, cancellationToken: ct));
        var peaks = await conn.QuerySingleAsync<DbPeaks>(new CommandDefinition("""
            SELECT max((payload->>'idle_in_tx_max_s')::float8) AS IdleInTx, max((payload->>'blocked')::float8) AS Blocked,
                   max((payload->>'longest_active_s')::float8) AS Longest, max((payload->>'conn_total')::float8) AS Conn
            FROM perf_pg_db_snapshot WHERE ts_utc >= @From AND ts_utc <= @To
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return new DbPeriodStats
        {
            Start = Parse(startRow?.Payload),
            End = Parse(endRow?.Payload),
            StartUtc = startRow?.Ts,
            EndUtc = endRow?.Ts,
            IdleInTxMaxS = peaks.IdleInTx,
            BlockedMax = peaks.Blocked,
            LongestActiveMaxS = peaks.Longest,
            ConnTotalMax = peaks.Conn,
        };

        static Dictionary<string, double?> Parse(string? json) =>
            string.IsNullOrEmpty(json)
                ? new Dictionary<string, double?>()
                : JsonSerializer.Deserialize<Dictionary<string, double?>>(json) ?? new Dictionary<string, double?>();
    }

    public async Task<IReadOnlyList<TableDelta>> TableDeltasAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TableDelta>(new CommandDefinition("""
            WITH b AS (
                SELECT COALESCE((SELECT max(ts_utc) FROM perf_pg_table_snapshot WHERE ts_utc <= @From),
                                (SELECT min(ts_utc) FROM perf_pg_table_snapshot WHERE ts_utc > @From AND ts_utc <= @To)) AS s,
                       (SELECT max(ts_utc) FROM perf_pg_table_snapshot WHERE ts_utc <= @To) AS e
            )
            SELECT e.relname AS Name, e.n_live AS Rows, e.n_dead AS Dead,
                   (s.relname IS NOT NULL) AS HasBaseline,
                   CASE WHEN s.relname IS NULL THEN 0 ELSE GREATEST(e.seq_scan - s.seq_scan, 0) END AS SeqScan,
                   CASE WHEN s.relname IS NULL THEN 0 ELSE GREATEST(e.seq_tup_read - s.seq_tup_read, 0) END AS SeqTupRead,
                   CASE WHEN s.relname IS NULL THEN 0 ELSE GREATEST(e.idx_scan - s.idx_scan, 0) END AS IdxScan,
                   CASE WHEN s.relname IS NULL THEN 0
                        ELSE GREATEST((e.n_ins + e.n_upd + e.n_del) - (s.n_ins + s.n_upd + s.n_del), 0) END AS Writes,
                   e.total_bytes AS TotalBytes,
                   e.total_bytes - COALESCE(s.total_bytes, e.total_bytes) AS BytesGrowth,
                   e.n_live - COALESCE(s.n_live, e.n_live) AS RowsGrowth,
                   CASE WHEN s.relname IS NOT NULL AND (e.heap_hit - s.heap_hit) + (e.heap_read - s.heap_read) > 0
                        THEN 100.0 * (e.heap_hit - s.heap_hit)
                             / ((e.heap_hit - s.heap_hit) + (e.heap_read - s.heap_read))
                   END AS HitPct
            FROM b
            JOIN perf_pg_table_snapshot e ON e.ts_utc = b.e
            LEFT JOIN perf_pg_table_snapshot s ON s.ts_utc = b.s AND s.relname = e.relname AND b.s < b.e
            ORDER BY e.total_bytes DESC
            """, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GrowthPoint>> TableGrowthAsync(DateTimeOffset from, DateTimeOffset to, int topTables, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GrowthPoint>(new CommandDefinition("""
            WITH top AS (
                SELECT relname FROM perf_pg_table_snapshot
                WHERE ts_utc = (SELECT max(ts_utc) FROM perf_pg_table_snapshot WHERE ts_utc <= @To)
                ORDER BY total_bytes DESC LIMIT @Top
            )
            SELECT relname AS Name,
                   date_bin(make_interval(secs => @Step), ts_utc, TIMESTAMPTZ '2000-01-01') AS T,
                   max(total_bytes)::bigint AS TotalBytes, max(n_live)::bigint AS Rows
            FROM perf_pg_table_snapshot
            WHERE ts_utc >= @From AND ts_utc <= @To AND relname IN (SELECT relname FROM top)
            GROUP BY relname, 2
            ORDER BY 2
            """, new
        {
            From = from.UtcDateTime,
            To = to.UtcDateTime,
            Top = topTables,
            Step = Math.Max(3600, StepFor(to - from)),
        }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<long> StorageBytesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT (COALESCE(sum(c.relpages), 0) * current_setting('block_size')::bigint)::bigint
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'i', 't')
              AND (c.relname LIKE 'perf\_%' OR c.relname LIKE 'ix\_perf\_%')
            """, cancellationToken: ct));
    }

    private sealed class StatusCounts { public long C4 { get; set; } public long C429 { get; set; } }
    private sealed class Bounds { public DateTime? Start { get; set; } public DateTime? End { get; set; } }
    private sealed class QueryTextRow { public long Id { get; set; } public string Query { get; set; } = ""; }
    private sealed class SnapshotPayload { public DateTime Ts { get; set; } public string Payload { get; set; } = ""; }
    private sealed class DbPeaks
    {
        public double? IdleInTx { get; set; }
        public double? Blocked { get; set; }
        public double? Longest { get; set; }
        public double? Conn { get; set; }
    }
    private sealed class VersionRouteRow
    {
        public string Version { get; set; } = "";
        public string Method { get; set; } = "";
        public string Route { get; set; } = "";
        public long Count { get; set; }
        public double MaxMs { get; set; }
        public long[] Hist { get; set; } = Array.Empty<long>();
    }
}

/// Delta between two pg_stat_statements snapshots. Statements absent from the
/// start snapshot count from their first appearance inside the period
/// (a slight undercount instead of the full lifetime total); a counter that
/// went down means stats were reset, so the end value is taken as-is.
public static class PgDelta
{
    public static List<PgStatementDelta> Compute(
        IReadOnlyList<PgStatementSnapshotRow> start,
        IReadOnlyList<PgStatementSnapshotRow> end,
        IReadOnlyList<PgStatementSnapshotRow> firstSeenInPeriod,
        bool cumulativeWhenNoStart)
    {
        var startMap = start.ToDictionary(s => s.QueryId);
        var firstMap = firstSeenInPeriod.GroupBy(f => f.QueryId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.TsUtc).First());
        var result = new List<PgStatementDelta>();
        foreach (var e in end)
        {
            PgStatementSnapshotRow? baseline = startMap.GetValueOrDefault(e.QueryId) ?? firstMap.GetValueOrDefault(e.QueryId);
            if (baseline is null && !cumulativeWhenNoStart) continue;
            var reset = baseline is not null && e.Calls < baseline.Calls;
            var b = reset || baseline is null ? null : baseline;
            var delta = new PgStatementDelta
            {
                QueryId = e.QueryId,
                Calls = e.Calls - (b?.Calls ?? 0),
                TotalMs = e.TotalMs - (b?.TotalMs ?? 0),
                Rows = e.Rows - (b?.Rows ?? 0),
                BlksHit = e.BlksHit - (b?.BlksHit ?? 0),
                BlksRead = e.BlksRead - (b?.BlksRead ?? 0),
                TempWritten = e.TempWritten - (b?.TempWritten ?? 0),
                StddevMs = e.StddevMs,
                MaxMs = e.MaxMs,
            };
            if (delta.Calls <= 0) continue;
            result.Add(delta);
        }
        return result.OrderByDescending(d => d.TotalMs).ToList();
    }
}
