using System.Reflection;
using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Servicedesk.Infrastructure.Performance;

/// Version of the running build, as shown in the UI footer (MinVer).
public static class PerfAppInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}

/// Persists one retired <see cref="PerfWindow"/>: binary COPY for the
/// per-key minute tables (hundreds of rows in one round trip), plain
/// inserts for the handful of single rows. Runs on the flush service only.
public sealed class PerfWriter
{
    private readonly NpgsqlDataSource _dataSource;

    public PerfWriter(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task WriteAsync(PerfWindow window, DateTimeOffset bucketUtc, string level, CancellationToken ct)
    {
        var bucket = bucketUtc.UtcDateTime;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        if (!window.Http.IsEmpty) await CopyHttpAsync(conn, window, bucket, ct);
        if (!window.Db.IsEmpty) await CopyDbAsync(conn, window, bucket, ct);
        if (!window.Spans.IsEmpty) await CopySpansAsync(conn, window, bucket, ct);
        if (!window.Rum.IsEmpty) await CopyRumAsync(conn, window, bucket, ct);
        if (!window.NPlusOne.IsEmpty) await CopyNPlusOneAsync(conn, window, bucket, ct);
        if (!window.Fingerprints.IsEmpty) await UpsertFingerprintsAsync(conn, window, ct);
        if (!window.SlowRequests.IsEmpty) await InsertSlowRequestsAsync(conn, window, ct);
        if (!window.WorkerRuns.IsEmpty) await InsertWorkerRunsAsync(conn, window, ct);
        await InsertRuntimeAsync(conn, window, bucket, level, ct);
    }

    private static async Task CopyHttpAsync(NpgsqlConnection conn, PerfWindow window, DateTime bucket, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY perf_http_minute (bucket_utc, method, route, status_class, count, err_count, sum_ms, max_ms, hist, " +
            "db_count, db_ms, ext_ms, pipeline_ms, bytes_sum, size_hist, users) FROM STDIN (FORMAT BINARY)", ct);

        // Per-minute API total ('*' row): feeds the timeline charts without
        // re-aggregating every route. Only /api routes count toward it.
        var total = new PerfAggregate(PerfWindow.HttpExtraCount, secondHistogram: true);
        var users = new HashSet<int>();
        foreach (var (key, a) in window.Http)
        {
            await WriteHttpRowAsync(w, bucket, key.Method, key.Route, key.StatusClass, a, a.DistinctUsers, ct);
            if (!key.Route.StartsWith("/api/", StringComparison.Ordinal)) continue;
            total.Count += a.Count;
            total.ErrorCount += a.ErrorCount;
            total.SumMicro += a.SumMicro;
            total.MaxMicro = Math.Max(total.MaxMicro, a.MaxMicro);
            PerfHistogram.Merge(total.Buckets, a.Buckets);
            PerfHistogram.Merge(total.Buckets2!, a.Buckets2!);
            for (var i = 0; i < a.Extras.Length; i++) total.Extras[i] += a.Extras[i];
        }
        if (total.Count > 0)
        {
            await WriteHttpRowAsync(w, bucket, "*", "*", "*", total, window.ActiveUsers.Count, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static async Task WriteHttpRowAsync(NpgsqlBinaryImporter w, DateTime bucket, string method, string route,
        string statusClass, PerfAggregate a, int users, CancellationToken ct)
    {
        await w.StartRowAsync(ct);
        await w.WriteAsync(bucket, NpgsqlDbType.TimestampTz, ct);
        await w.WriteAsync(method, NpgsqlDbType.Text, ct);
        await w.WriteAsync(route, NpgsqlDbType.Text, ct);
        await w.WriteAsync(statusClass, NpgsqlDbType.Text, ct);
        await w.WriteAsync(a.Count, NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(a.ErrorCount, NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(a.SumValue, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.MaxValue, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.Buckets, NpgsqlDbType.Array | NpgsqlDbType.Bigint, ct);
        // Extras hold raw values: counts, microseconds and bytes.
        await w.WriteAsync(a.Extras[PerfWindow.HttpDbCount], NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(a.Extras[PerfWindow.HttpDbMicro] / 1000.0, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.Extras[PerfWindow.HttpExtMicro] / 1000.0, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.Extras[PerfWindow.HttpPipelineMicro] / 1000.0, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.Extras[PerfWindow.HttpBytes], NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(a.Buckets2!, NpgsqlDbType.Array | NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(users, NpgsqlDbType.Integer, ct);
    }

    private static async Task CopyDbAsync(NpgsqlConnection conn, PerfWindow window, DateTime bucket, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY perf_db_query_minute (bucket_utc, fingerprint, source, count, err_count, sum_ms, max_ms, hist) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (key, a) in window.Db)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(bucket, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync(key.Fingerprint, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Source, NpgsqlDbType.Text, ct);
            await WriteStatsAsync(w, a, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static async Task CopySpansAsync(NpgsqlConnection conn, PerfWindow window, DateTime bucket, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY perf_span_minute (bucket_utc, kind, name, detail, count, err_count, sum_ms, max_ms, hist) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (key, a) in window.Spans)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(bucket, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync(key.Kind, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Name, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Detail, NpgsqlDbType.Text, ct);
            await WriteStatsAsync(w, a, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static async Task CopyRumAsync(NpgsqlConnection conn, PerfWindow window, DateTime bucket, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY perf_rum_minute (bucket_utc, route, metric, detail, device, connection, count, sum_value, max_value, hist) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (key, a) in window.Rum)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(bucket, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync(key.Route, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Metric, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Detail, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Device, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Connection, NpgsqlDbType.Text, ct);
            await w.WriteAsync(a.Count, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(a.SumValue, NpgsqlDbType.Double, ct);
            await w.WriteAsync(a.MaxValue, NpgsqlDbType.Double, ct);
            await w.WriteAsync(a.Buckets, NpgsqlDbType.Array | NpgsqlDbType.Bigint, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static async Task CopyNPlusOneAsync(NpgsqlConnection conn, PerfWindow window, DateTime bucket, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY perf_nplusone_minute (bucket_utc, route, fingerprint, requests, executions, max_per_request) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (key, a) in window.NPlusOne)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(bucket, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync(key.Route, NpgsqlDbType.Text, ct);
            await w.WriteAsync(key.Fingerprint, NpgsqlDbType.Text, ct);
            await w.WriteAsync(a.Count, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync((long)Math.Round(a.SumValue), NpgsqlDbType.Bigint, ct);
            await w.WriteAsync((long)Math.Round(a.MaxValue), NpgsqlDbType.Bigint, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static async Task WriteStatsAsync(NpgsqlBinaryImporter w, PerfAggregate a, CancellationToken ct)
    {
        await w.WriteAsync(a.Count, NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(a.ErrorCount, NpgsqlDbType.Bigint, ct);
        await w.WriteAsync(a.SumValue, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.MaxValue, NpgsqlDbType.Double, ct);
        await w.WriteAsync(a.Buckets, NpgsqlDbType.Array | NpgsqlDbType.Bigint, ct);
    }

    private static async Task UpsertFingerprintsAsync(NpgsqlConnection conn, PerfWindow window, CancellationToken ct)
    {
        // A fingerprint's text never changes (it is the hash input); the
        // caller is filled in the first time one is known.
        const string sql = """
            INSERT INTO perf_sql_fingerprint (fingerprint, sql_text, caller, first_seen_utc, last_seen_utc)
            SELECT f, s, c, now(), now()
            FROM unnest(@Fingerprints, @Sqls, @Callers) AS t(f, s, c)
            ON CONFLICT (fingerprint) DO UPDATE SET
                last_seen_utc = now(),
                caller = COALESCE(perf_sql_fingerprint.caller, EXCLUDED.caller)
            """;
        var items = window.Fingerprints.ToArray();
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Fingerprints = items.Select(i => i.Key).ToArray(),
            Sqls = items.Select(i => i.Value.Sql).ToArray(),
            Callers = items.Select(i => i.Value.Caller).ToArray(),
        }, cancellationToken: ct));
    }

    private static async Task InsertSlowRequestsAsync(NpgsqlConnection conn, PerfWindow window, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO perf_slow_request (ts_utc, method, route, status, total_ms, db_ms, db_count, ext_ms,
                                           pipeline_ms, gc_count, bytes, breakdown, trace_id)
            VALUES (@TimestampUtc, @Method, @Route, @Status, @TotalMs, @DbMs, @DbCount, @ExtMs,
                    @PipelineMs, @GcCount, @Bytes, CAST(@BreakdownJson AS jsonb), @TraceId)
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, window.SlowRequests.ToArray(), cancellationToken: ct));
    }

    private static async Task InsertWorkerRunsAsync(NpgsqlConnection conn, PerfWindow window, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO perf_worker_run (started_utc, worker, duration_ms, success, items, error_kind)
            SELECT * FROM unnest(@Started, @Workers, @Durations, @Successes, @Items, @Errors)
            """;
        var runs = window.WorkerRuns.ToArray();
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Started = runs.Select(r => r.StartedUtc.UtcDateTime).ToArray(),
            Workers = runs.Select(r => r.Worker).ToArray(),
            Durations = runs.Select(r => r.DurationMs).ToArray(),
            Successes = runs.Select(r => r.Success).ToArray(),
            Items = runs.Select(r => r.Items).ToArray(),
            Errors = runs.Select(r => r.ErrorKind).ToArray(),
        }, cancellationToken: ct));
    }

    private static async Task InsertRuntimeAsync(NpgsqlConnection conn, PerfWindow window, DateTime bucket, string level, CancellationToken ct)
    {
        var row = RuntimeMinuteRow.From(window, bucket, level, PerfAppInfo.Version);
        const string sql = """
            INSERT INTO perf_runtime_minute (
                bucket_utc, app_version, level, samples, requests, in_flight_max, active_users, overhead_ms,
                cpu_pct, cpu_pct_max, working_set_mb, gc_heap_mb, gc_heap_mb_max, alloc_mb, gen0, gen1, gen2,
                gc_pause_ms, interval_ms, tp_threads_avg, tp_threads_max, tp_queue_avg, tp_queue_max, tp_queue_samples,
                exceptions, lock_contentions, kestrel_active_max, kestrel_queued_max, signalr_conn_max,
                pool_busy_avg, pool_busy_max, pool_idle_avg, pool_max, pool_pending_max, pool_wait_samples, pool_timeouts,
                host_cpu_pct, host_cpu_max, host_steal_pct, host_iowait_pct, load1, host_cores, mem_total_mb,
                mem_available_mb, swap_used_mb, swap_in, swap_out, disk_await_ms, disk_util_pct, cg_throttled_pct,
                cg_mem_mb, cg_mem_limit_mb, oom_kills, root_free_pct, blob_free_pct, root_free_gb, blob_free_gb)
            VALUES (
                @BucketUtc, @AppVersion, @Level, @Samples, @Requests, @InFlightMax, @ActiveUsers, @OverheadMs,
                @CpuPct, @CpuPctMax, @WorkingSetMb, @GcHeapMb, @GcHeapMbMax, @AllocMb, @Gen0, @Gen1, @Gen2,
                @GcPauseMs, @IntervalMs, @TpThreadsAvg, @TpThreadsMax, @TpQueueAvg, @TpQueueMax, @TpQueueSamples,
                @Exceptions, @LockContentions, @KestrelActiveMax, @KestrelQueuedMax, @SignalRConnMax,
                @PoolBusyAvg, @PoolBusyMax, @PoolIdleAvg, @PoolMax, @PoolPendingMax, @PoolWaitSamples, @PoolTimeouts,
                @HostCpuPct, @HostCpuMax, @HostStealPct, @HostIoWaitPct, @Load1, @HostCores, @MemTotalMb,
                @MemAvailableMb, @SwapUsedMb, @SwapIn, @SwapOut, @DiskAwaitMs, @DiskUtilPct, @CgThrottledPct,
                @CgMemMb, @CgMemLimitMb, @OomKills, @RootFreePct, @BlobFreePct, @RootFreeGb, @BlobFreeGb)
            ON CONFLICT (bucket_utc) DO NOTHING
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));
    }

    public static string? SerializeBreakdown(object? breakdown) =>
        breakdown is null ? null : JsonSerializer.Serialize(breakdown, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
