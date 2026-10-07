using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Performance;

/// Admin-only Performance dashboard API (Settings → Performance).
///
/// Every mutation is audited (settings changes additionally through the
/// settings store's own <c>setting_changed</c> audit). The dashboard's own
/// routes are excluded from the HTTP statistics, so watching the dashboard
/// never skews it. Periods are capped at 400 days and never in the future.
public static class PerformanceEndpoints
{
    private static readonly TimeSpan OverviewCache = TimeSpan.FromSeconds(20);

    public static IEndpointRouteBuilder MapPerformanceEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/performance")
            .WithTags("Performance")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", async (IPerfSettings perf, PerfQueries queries, PgInspector pg, CancellationToken ct) =>
        {
            var now = perf.Time.GetUtcNow();
            var runtime = await queries.RuntimeSummaryAsync(now.AddMinutes(-15), now, 85, 150, ct);
            return Results.Ok(new
            {
                level = LevelName(perf.Level),
                baseLevel = LevelName(perf.Options.BaseLevel),
                diagnoseUntilUtc = perf.Level == PerfLevel.Diagnose ? perf.Options.DiagnoseUntilUtc : null,
                serverUtc = now,
                collectors = Collectors(perf.Options.Collectors),
                overhead = new
                {
                    msPerRequest = R(runtime.OverheadMsPerRequest, 4),
                    cpuPct = runtime.IntervalMs > 0 ? R(100 * runtime.OverheadMs / (runtime.IntervalMs * Environment.ProcessorCount), 3) : 0,
                    requests = runtime.Requests,
                },
                storageBytes = await queries.StorageBytesAsync(ct),
                hostSupported = HostMetricsReader.IsSupported,
                pgAccess = pg.Access,
                appVersion = PerfAppInfo.Version,
            });
        }).WithName("GetPerformanceStatus").WithOpenApi();

        admin.MapGet("/overview", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfDatasetBuilder builder,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var key = $"perf:overview:{f.ToUnixTimeSeconds() / 20}:{t.ToUnixTimeSeconds() / 20}";
            if (cache.TryGetValue(key, out object? cached) && cached is not null) return Results.Ok(cached);

            var d = await builder.BuildAsync(f, t, includeLive: true, compare: false, ct);
            var findings = PerfFindings.Evaluate(d);
            var scores = PerfFindings.Score(d, findings);
            var total = d.Total;
            var minutes = Math.Max(1, (t - f).TotalMinutes);
            var rum = d.Rum;
            var lcp = Merge(rum.Where(r => r.Metric == "lcp"));
            var inp = Merge(rum.Where(r => r.Metric == "inp"));
            var apiNet = rum.Where(r => r.Metric == "api_network").Sum(r => r.SumValue);
            var apiAll = rum.Where(r => r.Metric == "api_total").Sum(r => r.SumValue);

            var result = new
            {
                period = PeriodDto(d.Period),
                kpis = new
                {
                    requests = total?.Count ?? 0,
                    perMinute = R((total?.Count ?? 0) / minutes, 1),
                    p50 = R(total?.P50 ?? 0),
                    p95 = R(total?.P95 ?? 0),
                    p99 = R(total?.P99 ?? 0),
                    errorRate5xx = total is { Count: > 0 } ? R(100.0 * total.Errors / total.Count, 2) : 0,
                    errorRate4xx = total is { Count: > 0 } ? R(100.0 * total.Count4xx / total.Count, 2) : 0,
                    dbSharePct = R(total?.DbSharePct ?? 0, 1),
                    extSharePct = R(total?.ExtSharePct ?? 0, 1),
                    appCpuPct = N(d.Runtime.CpuPct, 1),
                    hostCpuPct = N(d.Runtime.HostCpuPct, 1),
                    stealPct = N(d.Runtime.StealPct, 2),
                    memAvailablePct = d.Runtime.MemTotalMb is > 0 && d.Runtime.MemAvailableMinMb is { } m ? R(100 * m / d.Runtime.MemTotalMb.Value, 1) : (double?)null,
                    lcpP75 = lcp is null ? (double?)null : R(lcp.Value.P75),
                    inpP75 = inp is null ? (double?)null : R(inp.Value.P75),
                    networkSharePct = apiAll > 0 ? R(100 * apiNet / apiAll, 1) : (double?)null,
                    activeUsersPeak = d.Runtime.ActiveUsersPeak,
                    diagnoseMinutes = d.Runtime.DiagnoseMinutes,
                },
                categories = scores,
                findings = findings.Select(FindingDto),
                budgets = d.Budgets.Select(b => new { b.Method, b.Route, b.TargetMs, b.Count, p95 = R(b.P95), b.Ok, b.HasData }),
                timeline = d.Timeline.Select(p => new
                {
                    t = p.T,
                    count = p.Count,
                    errors = p.Errors,
                    p50 = R(p.P50),
                    p95 = R(p.P95),
                    dbMs = p.Count > 0 ? R(p.DbMs / p.Count) : 0,
                    extMs = p.Count > 0 ? R(p.ExtMs / p.Count) : 0,
                    avgMs = p.Count > 0 ? R(p.SumMs / p.Count) : 0,
                    users = p.Users,
                }),
                workerRuns = d.WorkerRuns.Where(w => w.DurationMs >= 1000).Take(400).Select(WorkerRunDto),
                markers = d.Markers.Select(MarkerDto),
                versions = d.Versions.Where(v => v.LastUtc >= f.UtcDateTime),
                gaps = d.Gaps,
            };
            cache.Set(key, (object)result, OverviewCache);
            return Results.Ok(result);
        }).WithName("GetPerformanceOverview").WithOpenApi();

        admin.MapGet("/http", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var period = await q.ResolveAsync(f, t, ct);
            var routes = await q.HttpRoutesAsync(period, ct);
            var total = await q.HttpTotalAsync(period, ct);
            return Results.Ok(new
            {
                period = PeriodDto(period),
                total = total is null ? null : RouteDto(total),
                routes = routes.Select(RouteDto),
            });
        }).WithName("GetPerformanceHttp").WithOpenApi();

        admin.MapGet("/http/route", async (string method, string route, DateTimeOffset? from, DateTimeOffset? to,
            IPerfSettings perf, PerfQueries q, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            if (method.Length > 10 || route.Length > 200) return Results.BadRequest();
            var period = await q.ResolveAsync(f, t, ct);
            var seriesPeriod = await q.ResolveSeriesAsync(f, t, ct);
            var stats = (await q.HttpRoutesAsync(period, ct, method, route)).FirstOrDefault();
            var series = await q.HttpSeriesAsync(seriesPeriod, ct, method, route);
            var source = method + " " + route;
            var queries = await q.DbQueriesAsync(period, 25, ct, source);
            var nPlusOne = await q.NPlusOneAsync(period, ct, source);
            var slow = await q.SlowRequestsAsync(f, t, 50, ct, method, route);
            return Results.Ok(new
            {
                stats = stats is null ? null : RouteDto(stats),
                histogram = stats is null ? null : HistDto(stats.Hist),
                series = series.Select(p => new { t = p.T, p.Count, p.Errors, p50 = R(p.P50), p95 = R(p.P95), p99 = R(p.P99) }),
                queries = queries.Select(QueryDto),
                nPlusOne = nPlusOne.Select(NPlusOneDto),
                slow = slow.Select(SlowDto),
            });
        }).WithName("GetPerformanceRoute").WithOpenApi();

        admin.MapGet("/db/queries", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q,
            PgInspector pg, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var period = await q.ResolveAsync(f, t, ct);
            var app = await q.DbQueriesAsync(period, 100, ct);
            var nPlusOne = await q.NPlusOneAsync(period, ct);
            var pgss = await q.PgStatementDeltasAsync(f, t, ct);
            var bySource = (await q.SpansAsync(period, new[] { "db" }, ct)).Select(s => new
            {
                source = s.Name,
                s.Count,
                s.Errors,
                totalMs = R(s.SumMs),
                avgMs = R(s.AvgMs),
                p95 = R(s.P95),
            });
            return Results.Ok(new
            {
                access = pg.Access,
                diagnoseActive = perf.Level == PerfLevel.Diagnose,
                slowQueryMs = perf.Options.SlowQueryThresholdMs,
                app = app.Select(QueryDto),
                pgss = pgss.Take(100).Select(s => new
                {
                    queryId = s.QueryId.ToString(CultureInfo.InvariantCulture),
                    query = PerfRedactor.Sql(s.Query),
                    s.Calls,
                    totalMs = R(s.TotalMs),
                    meanMs = R(s.MeanMs),
                    maxMs = R(s.MaxMs),
                    stddevMs = R(s.StddevMs),
                    s.Rows,
                    hitPct = R(s.HitPct, 1),
                    s.BlksRead,
                    s.TempWritten,
                }),
                nPlusOne = nPlusOne.Select(NPlusOneDto),
                bySource,
            });
        }).WithName("GetPerformanceQueries").WithOpenApi();

        admin.MapGet("/db/tables", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q,
            PgInspector pg, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var tables = await pg.ReadTablesAsync(ct);
            var bloat = (await pg.ReadBloatAsync(ct)).ToDictionary(b => b.Name);
            var deltas = (await q.TableDeltasAsync(f, t, ct)).ToDictionary(d => d.Name);
            var indexes = await pg.ReadIndexesAsync(ct);
            var duplicates = await pg.ReadDuplicateIndexesAsync(ct);
            var growth = await q.TableGrowthAsync(t.AddDays(-30) < f ? t.AddDays(-30) : f, t, 8, ct);
            return Results.Ok(new
            {
                tables = tables.Select(x =>
                {
                    deltas.TryGetValue(x.Name, out var d);
                    // v0.1.26 — no snapshot at the period start: show "no data"
                    // instead of zeros (and never the cumulative counters).
                    if (d is { HasBaseline: false }) d = null;
                    bloat.TryGetValue(x.Name, out var b);
                    var heapTotal = x.HeapHit + x.HeapRead;
                    return new
                    {
                        name = x.Name,
                        rows = x.Rows,
                        dead = x.Dead,
                        deadPct = x.Rows > 0 ? R(100.0 * x.Dead / x.Rows, 1) : 0,
                        totalBytes = x.TotalBytes,
                        tableBytes = x.TableBytes,
                        indexBytes = x.IndexBytes,
                        bloatPct = b is null ? (double?)null : R(b.BloatPct, 1),
                        bloatBytes = b is null ? (double?)null : R(b.BloatBytes, 0),
                        seqScan = x.SeqScan,
                        idxScan = x.IdxScan,
                        periodSeqScan = d?.SeqScan,
                        periodIdxScan = d?.IdxScan,
                        periodSeqTupRead = d?.SeqTupRead,
                        periodWrites = d?.Writes,
                        bytesGrowth = d?.BytesGrowth,
                        rowsGrowth = d?.RowsGrowth,
                        modSinceAnalyze = x.ModSinceAnalyze,
                        hitPct = heapTotal > 0 ? R(100.0 * x.HeapHit / heapTotal, 2) : (double?)null,
                        lastVacuum = x.LastVacuum,
                        lastAutovacuum = x.LastAutovacuum,
                        lastAnalyze = x.LastAnalyze,
                        lastAutoanalyze = x.LastAutoanalyze,
                    };
                }),
                indexes = indexes.Select(i => new { i.Table, i.Index, i.Scans, i.Bytes, i.IsUnique, i.IsPrimary }),
                duplicates,
                growth = growth.GroupBy(g => g.Name).Select(g => new
                {
                    name = g.Key,
                    points = g.OrderBy(p => p.T).Select(p => new { t = p.T, bytes = p.TotalBytes, rows = p.Rows }),
                }),
            });
        }).WithName("GetPerformanceTables").WithOpenApi();

        admin.MapGet("/db/live", async (PgInspector pg, CancellationToken ct) =>
        {
            var activity = await pg.ReadActivityAsync(ct);
            var locks = await pg.ReadLocksAsync(ct);
            var stats = await pg.ReadDatabaseStatsAsync(ct);
            return Results.Ok(new { summary = Clean(stats), activity, locks });
        }).WithName("GetPerformanceDbLive").WithOpenApi();

        admin.MapGet("/db/config", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q,
            PgInspector pg, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var access = pg.Access.Checked ? pg.Access : await pg.CheckAccessAsync(ct);
            var settings = await pg.ReadSettingsAsync(ct);
            var io = await pg.ReadIoAsync(ct);
            var period = await q.PgDatabaseStatsAsync(f, t, ct);
            var current = await pg.ReadDatabaseStatsAsync(ct);
            string[] keys = { "blks_hit", "blks_read", "temp_files", "temp_bytes", "deadlocks", "xact_commit", "xact_rollback", "conflicts",
                              "checkpoints_timed", "checkpoints_req", "buffers_checkpoint", "buffers_clean", "buffers_backend", "maxwritten_clean" };
            return Results.Ok(new
            {
                access,
                settings,
                io,
                current = Clean(current),
                period = new
                {
                    fromUtc = period.StartUtc,
                    toUtc = period.EndUtc,
                    cacheHitPct = N(period.CacheHitPct, 3),
                    deltas = keys.ToDictionary(k => k, k => N(period.Delta(k), 0)),
                    idleInTxMaxS = N(period.IdleInTxMaxS, 1),
                    blockedMax = period.BlockedMax,
                    longestActiveMaxS = N(period.LongestActiveMaxS, 1),
                    connTotalMax = period.ConnTotalMax,
                },
            });
        }).WithName("GetPerformanceDbConfig").WithOpenApi();

        admin.MapPost("/db/plan", async (PlanRequest body, HttpContext http, IPerfSettings perf, PerfToolbox toolbox,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (body.Source is not ("pgss" or "app") || string.IsNullOrWhiteSpace(body.Id) || body.Id.Length > 40)
                return Results.BadRequest(new { error = "Invalid plan request." });
            var result = await toolbox.ExplainAsync(body.Source, body.Id, ct);
            await Audit(audit, http, "perf.plan", body.Source + ":" + body.Id, new { body.Source, body.Id, result.Ok }, ct);
            return Results.Ok(new { result.Ok, plan = result.PlanJson is null ? (JsonElement?)null : JsonDocument.Parse(result.PlanJson).RootElement, result.Error, result.Sql });
        }).WithName("PostPerformancePlan").WithOpenApi();

        admin.MapPost("/db/analyze", async (AnalyzeRequest body, HttpContext http, PerfToolbox toolbox, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await toolbox.AnalyzeAsync(body.Table ?? "", ct);
            await Audit(audit, http, "perf.analyze", body.Table, new { body.Table, result.Ok }, ct);
            return result.Ok ? Results.NoContent() : Results.BadRequest(new { error = result.Error });
        }).WithName("PostPerformanceAnalyze").WithOpenApi();

        admin.MapGet("/host", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q,
            PerfToolbox toolbox, ISettingsService settings, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var cpuHigh = await settings.GetAsync<int>(SettingKeys.Performance.FindingCpuPct, ct);
            var loadHigh = await settings.GetAsync<int>(SettingKeys.Performance.FindingLoadPctOfCores, ct);
            var summary = await q.RuntimeSummaryAsync(f, t, cpuHigh, loadHigh, ct);
            var series = await q.RuntimeSeriesAsync(f, t, PerfQueries.StepFor(t - f), ct);
            var benchmarks = await toolbox.BenchmarksAsync(ct);
            var (exSince, exTypes) = RuntimeSampler.ExceptionTypes(15);
            return Results.Ok(new
            {
                // v0.1.25 — type names only, since app start.
                exceptionTypes = new
                {
                    sinceUtc = exSince,
                    types = exTypes.Select(kv => new { type = PerfRedactor.Text(kv.Key), count = kv.Value }),
                },
                hostSupported = HostMetricsReader.IsSupported,
                processorCount = Environment.ProcessorCount,
                os = Environment.OSVersion.ToString(),
                dotnet = Environment.Version.ToString(),
                summary = new
                {
                    summary.CpuPct,
                    summary.CpuPctMax,
                    summary.HostCpuPct,
                    summary.HostCpuMax,
                    summary.StealPct,
                    summary.StealMax,
                    summary.IoWaitPct,
                    summary.Load1,
                    summary.Load1Max,
                    summary.HostCores,
                    summary.MemTotalMb,
                    summary.MemAvailableMinMb,
                    summary.SwapUsedMaxMb,
                    summary.SwapActivity,
                    summary.DiskAwaitMs,
                    summary.DiskAwaitMax,
                    summary.DiskUtilAvg,
                    summary.DiskUtilMax,
                    summary.CgThrottledPct,
                    summary.CgMemLimitMb,
                    summary.OomKillsDelta,
                    summary.RootFreePct,
                    summary.BlobFreePct,
                    summary.RootFreeGb,
                    summary.BlobFreeGb,
                    summary.WorkingSetMb,
                    summary.GcHeapMbStart,
                    summary.GcHeapMbEnd,
                    summary.GcHeapMbMax,
                    gcPausePct = R(summary.GcPausePct, 2),
                    threadPoolQueuePct = R(summary.ThreadPoolQueuePct, 1),
                    summary.TpThreadsStart,
                    summary.TpThreadsEnd,
                    summary.TpThreadsMax,
                    summary.Exceptions,
                    summary.LockContentions,
                    poolWaitPct = R(summary.PoolWaitPct, 1),
                    summary.PoolPendingMax,
                    summary.PoolTimeouts,
                    summary.PoolMax,
                    summary.PoolBusyMax,
                    summary.InFlightMax,
                    summary.KestrelQueuedMax,
                    summary.SignalRConnMax,
                    summary.Gen2,
                },
                series,
                benchmarks = benchmarks.Select(b => new { b.Id, t = b.TsUtc, b.CreatedBy, result = JsonDocument.Parse(b.Payload).RootElement }),
            });
        }).WithName("GetPerformanceHost").WithOpenApi();

        admin.MapPost("/benchmark", async (HttpContext http, PerfToolbox toolbox, IAuditLogger audit, CancellationToken ct) =>
        {
            var (actor, _) = ActorContext.Resolve(http);
            var result = await toolbox.BenchmarkAsync(actor, ct);
            await Audit(audit, http, "perf.benchmark", "server", result, ct);
            return Results.Ok(result);
        }).WithName("PostPerformanceBenchmark").WithOpenApi();

        admin.MapPost("/edge-check", async (HttpContext http, PerfToolbox toolbox, IAuditLogger audit, CancellationToken ct) =>
        {
            var items = await toolbox.EdgeCheckAsync(ct);
            await Audit(audit, http, "perf.edge-check", "nginx", null, ct);
            return Results.Ok(new { items });
        }).WithName("PostPerformanceEdgeCheck").WithOpenApi();

        admin.MapGet("/frontend", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var period = await q.ResolveAsync(f, t, ct);
            var seriesPeriod = await q.ResolveSeriesAsync(f, t, ct);
            var rum = await q.RumAsync(period, ct);
            var breakdown = await q.RumBreakdownAsync(period, ct);
            var heap = await q.RumSeriesAsync(seriesPeriod, "js_heap_mb", ct);
            var views = rum.Where(r => r.Metric == "view").GroupBy(r => r.Route).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
            RumStats? Get(string route, string metric) =>
                Merge(rum.Where(r => r.Route == route && r.Metric == metric)) is { } m
                    ? new RumStats { Route = route, Metric = metric, Count = m.Count, SumValue = m.Sum, MaxValue = m.Max, Hist = m.Hist }
                    : null;
            var routes = rum.Where(r => r.Metric is "lcp" or "inp" or "cls" or "fcp" or "ttfb" or "view" or "route_change" or "screen_api_calls")
                .Select(r => r.Route).Distinct().ToList();

            object? Vital(RumStats? s, bool cls = false) => s is null ? null : new
            {
                p75 = cls ? R(s.P75 / 1000, 3) : R(s.P75),
                count = s.Count,
            };

            return Results.Ok(new
            {
                routes = routes.Select(r => new
                {
                    route = r,
                    views = views.GetValueOrDefault(r),
                    lcp = Vital(Get(r, "lcp")),
                    inp = Vital(Get(r, "inp")),
                    cls = Vital(Get(r, "cls"), cls: true),
                    fcp = Vital(Get(r, "fcp")),
                    ttfb = Vital(Get(r, "ttfb")),
                    routeChange = Vital(Get(r, "route_change")),
                    apiCalls = Get(r, "screen_api_calls") is { } c ? new { p75 = R(c.P75, 0), avg = R(c.Avg, 1), max = R(c.MaxValue, 0) } : null,
                    apiKb = Get(r, "screen_api_kb") is { } kb ? new { p75 = R(kb.P75, 0), avg = R(kb.Avg, 0) } : null,
                    longTasks = rum.Where(x => x.Route == r && x.Metric == "long_task").Sum(x => x.Count),
                }).OrderByDescending(x => x.views),
                scripts = rum.Where(r => r.Metric == "loaf" && r.Detail.Length > 0).GroupBy(r => r.Detail).Select(g => new
                {
                    script = g.Key,
                    count = g.Sum(x => x.Count),
                    totalMs = R(g.Sum(x => x.SumValue)),
                    maxMs = R(g.Max(x => x.MaxValue)),
                }).OrderByDescending(x => x.totalMs).Take(25),
                api = rum.Where(r => r.Metric is "api_total" or "api_server" or "api_network" or "api_queue").GroupBy(r => r.Detail).Select(g =>
                {
                    var total = Merge(g.Where(x => x.Metric == "api_total"));
                    var server = Merge(g.Where(x => x.Metric == "api_server"));
                    var network = Merge(g.Where(x => x.Metric == "api_network"));
                    var queue = Merge(g.Where(x => x.Metric == "api_queue"));
                    return new
                    {
                        route = g.Key,
                        count = total?.Count ?? 0,
                        totalP75 = total is null ? 0 : R(total.Value.P75),
                        serverP75 = server is null ? 0 : R(server.Value.P75),
                        networkP75 = network is null ? 0 : R(network.Value.P75),
                        networkPct = total is { Sum: > 0 } && network is not null ? R(100 * network.Value.Sum / total.Value.Sum, 1) : 0,
                        queueP75 = queue is null ? 0 : R(queue.Value.P75),
                        queuePct = total is { Sum: > 0 } && queue is not null ? R(100 * queue.Value.Sum / total.Value.Sum, 1) : 0,
                    };
                }).OrderByDescending(x => x.count).Take(50),
                navigation = new[] { "nav_dns", "nav_tcp", "nav_tls", "nav_ttfb", "nav_download", "nav_dom", "nav_load" }.Select(m =>
                {
                    var s = Merge(rum.Where(r => r.Metric == m));
                    return new { metric = m, p75 = s is null ? (double?)null : R(s.Value.P75), count = s?.Count ?? 0 };
                }),
                breakdown,
                heap = heap.Select(h => new { t = h.T, mb = R(h.Value, 1) }),
                reconnects = rum.Where(r => r.Metric == "signalr_reconnect").Sum(r => r.Count),
                bundle = PerfDatasetBuilder.ReadBundle(),
            });
        }).WithName("GetPerformanceFrontend").WithOpenApi();

        admin.MapGet("/background", async (DateTimeOffset? from, DateTimeOffset? to, IPerfSettings perf, PerfQueries q, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var period = await q.ResolveAsync(f, t, ct);
            var seriesPeriod = await q.ResolveSeriesAsync(f, t, ct);
            var workers = await q.WorkerSummaryAsync(f, t, ct);
            var runs = await q.WorkerRunsAsync(f, t, 1500, ct);
            var spans = await q.SpansAsync(period, new[] { "external", "hub", "hub-connection", "broadcast", "search" }, ct);
            var connections = await q.RuntimeSeriesAsync(f, t, seriesPeriod.StepSeconds, ct);
            IReadOnlyList<WorkerOverlap> overlaps = Array.Empty<WorkerOverlap>();
            if (t - f <= TimeSpan.FromDays(2) && runs.Count > 0)
            {
                overlaps = PerfDatasetBuilder.ComputeOverlaps(await q.HttpMinuteTotalsAsync(f, t, ct), runs);
            }
            return Results.Ok(new
            {
                workers,
                runs = runs.Select(WorkerRunDto),
                overlaps = overlaps.Select(o => new { o.Worker, o.MinutesDuring, o.RequestsDuring, p95During = R(o.P95During), p95Outside = R(o.P95Outside) }),
                external = spans.Where(s => s.Kind == "external").GroupBy(s => s.Name).Select(g =>
                {
                    var hist = PerfHistogram.Merge(g.Select(x => (IReadOnlyList<long>)x.Hist));
                    var max = g.Max(x => x.MaxMs);
                    var count = g.Sum(x => x.Count);
                    return new
                    {
                        host = g.Key,
                        count,
                        errors = g.Sum(x => x.Errors),
                        throttled = g.Where(x => x.Detail.EndsWith("429", StringComparison.Ordinal)).Sum(x => x.Count),
                        p50 = R(PerfHistogram.Percentile(hist, 50, max)),
                        p95 = R(PerfHistogram.Percentile(hist, 95, max)),
                        maxMs = R(max),
                        totalMs = R(g.Sum(x => x.SumMs)),
                        details = g.Select(x => new { x.Detail, x.Count }),
                    };
                }).OrderByDescending(x => x.totalMs),
                hubs = spans.Where(s => s.Kind == "hub").Select(SpanDto),
                connections = spans.Where(s => s.Kind == "hub-connection").Select(s => new { hub = s.Name, s.Detail, s.Count }),
                broadcasts = spans.Where(s => s.Kind == "broadcast").Select(s => new { hub = s.Name, s.Detail, s.Count }).OrderByDescending(x => x.Count),
                search = spans.Where(s => s.Kind == "search").Select(SpanDto),
                connectionSeries = connections.Select(c => new { t = c.T, signalr = c.SignalR, kestrel = c.KestrelActive }),
            });
        }).WithName("GetPerformanceBackground").WithOpenApi();

        // ------------------------------------------------------- markers

        admin.MapPost("/markers", async (MarkerRequest body, HttpContext http, PerfQueries q, IAuditLogger audit, CancellationToken ct) =>
        {
            var label = (body.Label ?? "").Trim();
            if (label.Length is 0 or > 120) return Results.BadRequest(new { error = "A marker needs a label of at most 120 characters." });
            var (actor, _) = ActorContext.Resolve(http);
            var id = await q.AddMarkerAsync("manual", label, actor, ct);
            await Audit(audit, http, "perf.marker.add", id.ToString(CultureInfo.InvariantCulture), new { label }, ct);
            return Results.Ok(new { id });
        }).WithName("PostPerformanceMarker").WithOpenApi();

        admin.MapDelete("/markers/{id:long}", async (long id, HttpContext http, PerfQueries q, IAuditLogger audit, CancellationToken ct) =>
        {
            var deleted = await q.DeleteMarkerAsync(id, ct);
            if (deleted) await Audit(audit, http, "perf.marker.delete", id.ToString(CultureInfo.InvariantCulture), null, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithName("DeletePerformanceMarker").WithOpenApi();

        // ------------------------------------------------- settings / mode

        admin.MapGet("/settings", async (ISettingsService settings, CancellationToken ct) =>
        {
            var entries = await settings.ListAsync("Performance", ct);
            return Results.Ok(new
            {
                settings = entries
                    .Where(e => e.Key != SettingKeys.Performance.DiagnoseUntilUtc)
                    .Select(e => new { e.Key, e.Value, e.ValueType, e.Description, e.DefaultValue }),
            });
        }).WithName("GetPerformanceSettings").WithOpenApi();

        admin.MapPut("/settings", async (Dictionary<string, string> body, HttpContext http, ISettingsService settings,
            IPerfSettings perf, CancellationToken ct) =>
        {
            if (body.Count == 0 || body.Count > 100) return Results.BadRequest(new { error = "Nothing to save." });
            var (actor, role) = ActorContext.Resolve(http);
            var known = SettingDefaults.All.Where(d => d.Category == "Performance").ToDictionary(d => d.Key);
            foreach (var (key, value) in body)
            {
                if (!known.ContainsKey(key) || key == SettingKeys.Performance.DiagnoseUntilUtc)
                    return Results.BadRequest(new { error = $"Unknown or read-only setting '{key}'." });
                if (key == SettingKeys.Performance.Level && value is not ("off" or "basic"))
                    return Results.BadRequest(new { error = "Level must be 'off' or 'basic'." });
                if (key == SettingKeys.Performance.Budgets && (value.Length > 4000 || Budget.Parse(value).Count == 0 && value.Trim().Length > 0))
                    return Results.BadRequest(new { error = "Budgets must be lines like 'GET /api/tickets=500'." });
                if (value.Length > 4000) return Results.BadRequest(new { error = $"Value for '{key}' is too long." });
            }
            try
            {
                foreach (var (key, value) in body)
                {
                    await settings.SetAsync(key, value, actor, role, ct);
                }
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            await perf.RefreshAsync(ct);
            return Results.NoContent();
        }).WithName("PutPerformanceSettings").WithOpenApi();

        admin.MapPost("/diagnose", async (DiagnoseRequest body, HttpContext http, ISettingsService settings, IPerfSettings perf,
            PerfQueries q, IAuditLogger audit, CancellationToken ct) =>
        {
            if (perf.Options.BaseLevel == PerfLevel.Off)
                return Results.Conflict(new { error = "Switch monitoring on (Basic) before starting Diagnose mode." });
            var max = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Performance.DiagnoseMaxMinutes, ct), 5, 1440);
            var fallback = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Performance.DiagnoseDefaultMinutes, ct), 5, 1440);
            var minutes = Math.Clamp(body.Minutes ?? fallback, 5, max);
            var until = perf.Time.GetUtcNow().AddMinutes(minutes);
            var (actor, role) = ActorContext.Resolve(http);
            await settings.SetAsync(SettingKeys.Performance.DiagnoseUntilUtc, PerfSettingsProvider.FormatUtc(until), actor, role, ct);
            await perf.RefreshAsync(ct);
            await q.AddMarkerAsync("diagnose", $"Diagnose started ({minutes} min)", actor, ct);
            await Audit(audit, http, "perf.diagnose.start", "performance", new { minutes, until }, ct);
            return Results.Ok(new { diagnoseUntilUtc = until });
        }).WithName("PostPerformanceDiagnose").WithOpenApi();

        admin.MapDelete("/diagnose", async (HttpContext http, ISettingsService settings, IPerfSettings perf, PerfQueries q,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (actor, role) = ActorContext.Resolve(http);
            await settings.SetAsync(SettingKeys.Performance.DiagnoseUntilUtc, "", actor, role, ct);
            await perf.RefreshAsync(ct);
            await q.AddMarkerAsync("diagnose", "Diagnose stopped", actor, ct);
            await Audit(audit, http, "perf.diagnose.stop", "performance", null, ct);
            return Results.NoContent();
        }).WithName("DeletePerformanceDiagnose").WithOpenApi();

        admin.MapDelete("/data", async (HttpContext http, PerfToolbox toolbox, IAuditLogger audit, CancellationToken ct) =>
        {
            await toolbox.ClearAsync(ct);
            await Audit(audit, http, "perf.data.clear", "performance", null, ct);
            return Results.NoContent();
        }).WithName("DeletePerformanceData").WithOpenApi();

        // ----------------------------------------------------------- export

        admin.MapGet("/export", async (DateTimeOffset? from, DateTimeOffset? to, string? format, bool? compare, bool? plans,
            HttpContext http, IPerfSettings perf, PerfDatasetBuilder builder, PerfReportBuilder reports, PerfToolbox toolbox,
            ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!TryPeriod(perf, from, to, out var f, out var t, out var error)) return error!;
            var fmt = (format ?? "zip").ToLowerInvariant();
            if (fmt is not ("zip" or "md" or "json")) return Results.BadRequest(new { error = "format must be zip, md or json." });

            var d = await builder.BuildAsync(f, t, includeLive: true, compare: compare == true, ct);
            var findings = PerfFindings.Evaluate(d);
            var scores = PerfFindings.Score(d, findings);

            var planMap = new Dictionary<string, string>();
            if (plans == true && fmt == "zip")
            {
                foreach (var s in d.PgStatements.Take(8))
                {
                    var plan = await toolbox.ExplainAsync("pgss", s.QueryId.ToString(CultureInfo.InvariantCulture), ct);
                    if (plan.Ok && plan.PlanJson is not null) planMap["pgss:" + s.QueryId] = plan.PlanJson;
                }
                foreach (var q in d.Queries.Take(5))
                {
                    var plan = await toolbox.ExplainAsync("app", q.Fingerprint, ct);
                    if (plan.Ok && plan.PlanJson is not null) planMap["app:" + q.Fingerprint] = plan.PlanJson;
                }
            }

            var tz = await settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
            var markdown = reports.BuildMarkdown(d, findings, scores, tz, planMap);
            await Audit(audit, http, "perf.export", fmt, new { from = f, to = t, format = fmt, compare = compare == true, plans = planMap.Count }, ct);

            var stamp = t.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            switch (fmt)
            {
                case "md":
                    return Results.File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", $"servicedesk-performance-{stamp}.md");
                case "json":
                    return Results.File(Encoding.UTF8.GetBytes(reports.BuildJson(d, findings, scores)), "application/json", $"servicedesk-performance-{stamp}.json");
                default:
                    var zip = reports.BuildZip(markdown, reports.BuildJson(d, findings, scores), planMap);
                    return Results.File(zip, "application/zip", $"servicedesk-performance-{stamp}.zip");
            }
        }).WithName("GetPerformanceExport").WithOpenApi();

        return app;
    }

    // ---------------------------------------------------------------- DTOs

    public sealed record PlanRequest(string Source, string Id);
    public sealed record AnalyzeRequest(string? Table);
    public sealed record MarkerRequest(string? Label);
    public sealed record DiagnoseRequest(int? Minutes);

    internal static bool TryPeriod(IPerfSettings perf, DateTimeOffset? from, DateTimeOffset? to,
        out DateTimeOffset f, out DateTimeOffset t, out IResult? error)
    {
        var now = perf.Time.GetUtcNow();
        t = (to ?? now).ToUniversalTime();
        f = (from ?? t.AddHours(-24)).ToUniversalTime();
        error = null;
        if (t > now.AddMinutes(5)) t = now;
        if (f >= t || t - f > TimeSpan.FromDays(400) || f < now.AddDays(-400))
        {
            error = Results.BadRequest(new { error = "Choose a period of at most 400 days that ends now or earlier." });
            return false;
        }
        return true;
    }

    private static Task Audit(IAuditLogger audit, HttpContext http, string eventType, string? target, object? payload, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);
    }

    private static string LevelName(PerfLevel level) => level.ToString().ToLowerInvariant();

    private static object Collectors(PerfCollector c) => new
    {
        http = c.HasFlag(PerfCollector.Http),
        database = c.HasFlag(PerfCollector.Database),
        postgres = c.HasFlag(PerfCollector.Postgres),
        runtime = c.HasFlag(PerfCollector.Runtime),
        host = c.HasFlag(PerfCollector.Host),
        frontend = c.HasFlag(PerfCollector.Frontend),
        signalR = c.HasFlag(PerfCollector.SignalR),
        workers = c.HasFlag(PerfCollector.Workers),
        external = c.HasFlag(PerfCollector.External),
    };

    private static object PeriodDto(PerfPeriod p) => new { from = p.From, to = p.To, p.UsesHourly, p.StepSeconds };

    internal static object RouteDto(HttpRouteStats r) => new
    {
        method = r.Method,
        route = r.Route,
        count = r.Count,
        errors5xx = r.Errors,
        count4xx = r.Count4xx,
        count429 = r.Count429,
        p50 = R(r.P50),
        p95 = R(r.P95),
        p99 = R(r.P99),
        maxMs = R(r.MaxMs),
        avgMs = R(r.AvgMs),
        avgDbMs = R(r.AvgDbMs),
        avgDbQueries = R(r.AvgDbQueries, 1),
        avgExtMs = R(r.AvgExtMs),
        avgPipelineMs = R(r.AvgPipelineMs),
        avgAppMs = R(r.AvgAppMs),
        avgKb = R(r.AvgKb, 1),
        p95Kb = R(r.P95Kb, 1),
        totalMs = R(r.SumMs),
        dbSharePct = R(r.DbSharePct, 1),
        extSharePct = R(r.ExtSharePct, 1),
        pipelineSharePct = R(r.PipelineSharePct, 1),
        appSharePct = R(r.AppSharePct, 1),
        users = r.Users,
    };

    private static object QueryDto(DbQueryStats q) => new
    {
        fingerprint = q.Fingerprint,
        sql = PerfRedactor.Sql(q.Sql),
        caller = q.Caller,
        count = q.Count,
        errors = q.Errors,
        totalMs = R(q.SumMs),
        avgMs = R(q.AvgMs),
        p95 = R(q.P95),
        maxMs = R(q.MaxMs),
        sources = q.Sources.Select(s => new { source = s.Source, count = s.Count, totalMs = R(s.SumMs) }),
    };

    private static object NPlusOneDto(NPlusOneStats n) => new
    {
        route = n.Route,
        fingerprint = n.Fingerprint,
        sql = n.Sql is null ? null : PerfRedactor.Sql(n.Sql),
        caller = n.Caller,
        requests = n.Requests,
        executions = n.Executions,
        maxPerRequest = n.MaxPerRequest,
    };

    private static object SlowDto(SlowRequestRow s) => new
    {
        id = s.Id,
        t = s.TsUtc,
        method = s.Method,
        route = s.Route,
        status = s.Status,
        totalMs = s.TotalMs,
        dbMs = s.DbMs,
        dbCount = s.DbCount,
        extMs = s.ExtMs,
        pipelineMs = s.PipelineMs,
        gcCount = s.GcCount,
        bytes = s.Bytes,
        breakdown = s.Breakdown is null ? (JsonElement?)null : JsonDocument.Parse(s.Breakdown).RootElement,
        traceId = s.TraceId,
    };

    private static object WorkerRunDto(WorkerRunRow w) => new
    {
        t = w.StartedUtc,
        worker = w.Worker,
        durationMs = R(w.DurationMs),
        success = w.Success,
        items = w.Items,
        error = w.ErrorKind,
    };

    private static object MarkerDto(MarkerRow m) => new { id = m.Id, t = m.TsUtc, kind = m.Kind, label = m.Label, createdBy = m.CreatedBy };

    private static object SpanDto(SpanStats s) => new
    {
        name = s.Name,
        detail = s.Detail,
        count = s.Count,
        errors = s.Errors,
        avgMs = R(s.AvgMs),
        p50 = R(s.P50),
        p95 = R(s.P95),
        maxMs = R(s.MaxMs),
        totalMs = R(s.SumMs),
    };

    private static object FindingDto(PerfFinding f) => new
    {
        key = f.Key,
        category = f.Category,
        severity = f.Severity,
        title = f.Title,
        explanation = f.Explanation,
        evidence = f.Evidence,
        cause = f.Cause,
        action = f.Action,
        codeRefs = f.CodeRefs.Select(r => new { r.Kind, r.Value, sql = r.Sql is null ? null : PerfRedactor.Sql(r.Sql), r.Caller }),
        impactMs = R(f.ImpactMs, 0),
        impactLabel = f.ImpactLabel,
    };

    private static object HistDto(long[] hist) => hist.Select((count, i) => new
    {
        le = i < PerfHistogram.Bounds.Length ? PerfHistogram.Bounds[i] : (double?)null,
        count,
    }).Where(x => x.count > 0);

    private readonly record struct Merged(long Count, double Sum, double Max, long[] Hist)
    {
        public double P75 => PerfHistogram.Percentile(Hist, 75, Max);
    }

    private static Merged? Merge(IEnumerable<RumStats> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return null;
        return new Merged(list.Sum(r => r.Count), list.Sum(r => r.SumValue), list.Max(r => r.MaxValue),
            PerfHistogram.Merge(list.Select(r => (IReadOnlyList<long>)r.Hist)));
    }

    private static Dictionary<string, double?> Clean(Dictionary<string, double?> map) =>
        map.ToDictionary(kv => kv.Key, kv => kv.Value is { } v && double.IsFinite(v) ? Math.Round(v, 3) : (double?)null);

    private static double R(double value, int digits = 2) => double.IsFinite(value) ? Math.Round(value, digits) : 0;

    private static double? N(double? value, int digits = 2) => value is { } v && double.IsFinite(v) ? Math.Round(v, digits) : null;
}
