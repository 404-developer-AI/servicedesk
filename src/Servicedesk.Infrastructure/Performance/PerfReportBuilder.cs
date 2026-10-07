using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servicedesk.Infrastructure.Performance;

/// Builds the "give this to Claude Code" export: a compact Markdown report
/// (top-N tables, findings with code pointers) plus the same data as JSON,
/// optionally zipped with generic query plans.
///
/// Privacy: the dataset only ever holds route templates, normalised SQL,
/// table/index names and numbers. Every free-text field still goes through
/// <see cref="PerfRedactor"/> on the way out (belt and braces), and the
/// report states explicitly that it contains no personal data.
public sealed class PerfReportBuilder
{
    public const int SchemaVersion = 1;
    private const int MaxMarkdownBytes = 100 * 1024;
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public string BuildMarkdown(PerfDataset d, IReadOnlyList<PerfFinding> findings, IReadOnlyList<PerfCategoryScore> scores,
        string timeZoneId, IReadOnlyDictionary<string, string>? plans = null)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return BuildMarkdownCore(d, findings, scores, timeZoneId, plans);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private string BuildMarkdownCore(PerfDataset d, IReadOnlyList<PerfFinding> findings, IReadOnlyList<PerfCategoryScore> scores,
        string timeZoneId, IReadOnlyDictionary<string, string>? plans)
    {
        var tz = ResolveZone(timeZoneId);
        var sb = new StringBuilder();
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("yyyy-MM-dd HH:mm", C);

        var diagPct = d.Runtime.Minutes == 0 ? 0 : 100.0 * d.Runtime.DiagnoseMinutes / d.Runtime.Minutes;
        sb.AppendLine("# Servicedesk performance report");
        sb.AppendLine();
        sb.AppendLine($"Period: {Local(d.Period.From)} – {Local(d.Period.To)} ({tz.Id}) · App version: {d.AppVersion} · " +
                      $"Generated: {Local(d.GeneratedUtc)} · Monitoring level in period: Basic/Diagnose ({diagPct:0}% Diagnose)");
        sb.AppendLine();
        sb.AppendLine("## Instructions for Claude Code");
        sb.AppendLine("- This report comes from the built-in performance monitor of this repository (Settings → Performance).");
        sb.AppendLine("- Start at \"Top bottlenecks\". Every bottleneck points to route templates, query fingerprints, tables or workers; " +
                      "look them up in the code: route templates are mapped in the `Map*Endpoints` files under `src/Servicedesk.Api`, " +
                      "SQL lives in Dapper calls under `src/Servicedesk.Infrastructure` (the *caller* column names the method that issued the query), " +
                      "frontend routes are TanStack Router paths in `src/Servicedesk.Web/src/app/router.tsx`.");
        sb.AppendLine("- Propose one concrete change per bottleneck (index, query rewrite, batching instead of N+1, caching, async fix, " +
                      "smaller payload, frontend change) and say how to verify the improvement with this dashboard.");
        sb.AppendLine("- Where the bottleneck is the hosting (CPU steal, RAM, disk), say so explicitly instead of changing code.");
        sb.AppendLine("- Schema changes go in `DatabaseBootstrapper` (idempotent raw SQL), never EF migrations.");
        sb.AppendLine("- The report contains no personal data: no ids, names, e-mail addresses, ticket content or SQL parameter values.");
        sb.AppendLine();

        // ------------------------------------------------------ environment
        sb.AppendLine("## Environment");
        var rt = d.Runtime;
        var env = new List<(string, string)>
        {
            ("Host", rt.HostCores is null ? "not measured (non-Linux or host collector off)" :
                $"{rt.HostCores} cores, {Num(rt.MemTotalMb)} MB RAM, swap max used {Num(rt.SwapUsedMaxMb)} MB"),
            ("Free disk (min)", $"root {Pct(rt.RootFreePct)}, blob storage {Pct(rt.BlobFreePct)}"),
            ("PostgreSQL", d.PgAccess.Checked ? $"{d.PgAccess.ServerVersion} (pg_monitor: {(d.PgAccess.PgMonitor ? "yes" : "no")}, pg_stat_statements: {(d.PgAccess.StatStatements ? "yes" : "no")})" : "unknown"),
            ("Npgsql MaxPoolSize", d.MaxPoolSize.ToString(C)),
            (".NET", Environment.Version.ToString()),
            ("Active users (peak per minute)", rt.ActiveUsersPeak.ToString(C)),
            ("Requests (API) in period", PerfFormat.Count(d.Total?.Count ?? 0)),
            ("Monitoring overhead", $"{rt.OverheadMsPerRequest:0.000} ms per request"),
        };
        foreach (var s in d.PgSettings.Where(s => s.Name is "shared_buffers" or "work_mem" or "effective_cache_size" or "random_page_cost" or "max_connections"))
        {
            env.Add(("pg " + s.Name, s.Setting + (s.Unit is null ? "" : " " + s.Unit)));
        }
        Table(sb, new[] { "Item", "Value" }, env.Select(e => new[] { e.Item1, e.Item2 }));

        // --------------------------------------------------------- summary
        sb.AppendLine("## Summary per category");
        Table(sb, new[] { "Category", "Status", "Score", "Conclusion" },
            scores.Select(s => new[] { s.Label, s.Status, s.Score < 0 ? "–" : s.Score.ToString(C), Safe(s.Summary) }));

        if (d.Budgets.Count > 0)
        {
            sb.AppendLine("### Performance budgets");
            Table(sb, new[] { "Flow", "Target", "p95", "Requests", "OK" },
                d.Budgets.Select(b => new[] { $"`{b.Method} {b.Route}`", PerfFormat.Ms(b.TargetMs), b.HasData ? PerfFormat.Ms(b.P95) : "–", PerfFormat.Count(b.Count), b.HasData ? (b.Ok ? "yes" : "**no**") : "no data" }));
        }

        // -------------------------------------------------------- findings
        sb.AppendLine("## Top bottlenecks (sorted by impact)");
        sb.AppendLine("Impact ≈ extra waiting time per occurrence × number of occurrences in the period.");
        sb.AppendLine();
        var n = 0;
        foreach (var f in findings.Take(25))
        {
            n++;
            sb.AppendLine($"### {n}. {Safe(f.Title)}");
            sb.AppendLine($"- Category: {f.Category} · Severity: {f.Severity} · Impact: {f.ImpactLabel}");
            sb.AppendLine($"- What it means: {Safe(f.Explanation)}");
            sb.AppendLine($"- Evidence: {string.Join("; ", f.Evidence.Select(e => $"{Safe(e.Label)}: {Safe(e.Value)}"))}");
            sb.AppendLine($"- Likely cause: {Safe(f.Cause)}");
            sb.AppendLine($"- Recommended action: {Safe(f.Action)}");
            if (f.CodeRefs.Count > 0)
            {
                sb.AppendLine("- Where to look:");
                foreach (var r in f.CodeRefs)
                {
                    sb.AppendLine($"  - {r.Kind} `{Safe(r.Value)}`" + (r.Caller is null ? "" : $" — caller `{Safe(r.Caller)}`"));
                    if (!string.IsNullOrEmpty(r.Sql))
                    {
                        sb.AppendLine("    ```sql");
                        sb.AppendLine("    " + Clip(PerfRedactor.Sql(r.Sql), 1200).Replace("\n", "\n    "));
                        sb.AppendLine("    ```");
                    }
                }
            }
            sb.AppendLine();
        }
        if (n == 0) sb.AppendLine("No bottlenecks detected in this period.").AppendLine();

        // ---------------------------------------------------------- routes
        sb.AppendLine("## API routes (top 25 by total time)");
        Table(sb, new[] { "Route", "Requests", "p50", "p95", "p99", "5xx", "DB q/req", "DB ms", "Ext ms", "Own ms", "Avg KB" },
            d.ApiRoutes.OrderByDescending(r => r.SumMs).Take(25).Select(r => new[]
            {
                $"`{r.Key}`", PerfFormat.Count(r.Count), PerfFormat.Ms(r.P50), PerfFormat.Ms(r.P95), PerfFormat.Ms(r.P99),
                PerfFormat.Count(r.Errors), r.AvgDbQueries.ToString("0.0", C), PerfFormat.Ms(r.AvgDbMs), PerfFormat.Ms(r.AvgExtMs),
                PerfFormat.Ms(r.AvgAppMs), r.AvgKb.ToString("0", C),
            }));

        sb.AppendLine("## Slow requests (top 20 with breakdown)");
        Table(sb, new[] { "When", "Route", "Status", "Total", "DB (queries)", "External", "Pipeline", "GCs", "Top queries" },
            d.SlowRequests.Take(20).Select(s => new[]
            {
                Local(new DateTimeOffset(s.TsUtc, TimeSpan.Zero)), $"`{s.Method} {s.Route}`", s.Status.ToString(C), PerfFormat.Ms(s.TotalMs),
                $"{PerfFormat.Ms(s.DbMs)} ({s.DbCount})", PerfFormat.Ms(s.ExtMs), PerfFormat.Ms(s.PipelineMs), s.GcCount.ToString(C),
                TopQueries(s.Breakdown),
            }));

        // ------------------------------------------------------------- DB
        sb.AppendLine("## Database queries");
        sb.AppendLine("### App-measured (top 25 by total time)");
        if (d.Queries.Count == 0)
        {
            sb.AppendLine("No app-side query capture in this period (per-query capture runs in Diagnose mode).").AppendLine();
        }
        foreach (var q in d.Queries.Take(25))
        {
            sb.AppendLine($"- `{q.Fingerprint}` · {PerfFormat.Count(q.Count)} calls · avg {PerfFormat.Ms(q.AvgMs)} · p95 {PerfFormat.Ms(q.P95)} · total {PerfFormat.Duration(q.SumMs)}" +
                          (q.Caller is null ? "" : $" · caller `{Safe(q.Caller)}`") +
                          (q.Sources.Count == 0 ? "" : $" · from {string.Join(", ", q.Sources.Take(3).Select(s => "`" + Safe(s.Source) + "`"))}"));
            sb.AppendLine("  ```sql");
            sb.AppendLine("  " + Clip(PerfRedactor.Sql(q.Sql), 900).Replace("\n", "\n  "));
            sb.AppendLine("  ```");
        }
        sb.AppendLine();
        sb.AppendLine("### pg_stat_statements (top 25 by total time in period)");
        if (d.PgStatements.Count == 0)
        {
            sb.AppendLine(d.PgAccess.StatStatementsReason ?? "No pg_stat_statements snapshots in this period.").AppendLine();
        }
        foreach (var s in d.PgStatements.Take(25))
        {
            sb.AppendLine($"- queryid `{s.QueryId}` · {PerfFormat.Count(s.Calls)} calls · mean {PerfFormat.Ms(s.MeanMs)} · total {PerfFormat.Duration(s.TotalMs)} · " +
                          $"rows {PerfFormat.Count(s.Rows)} · cache hit {PerfFormat.Pct(s.HitPct)}" + (s.TempWritten > 0 ? $" · temp {PerfFormat.Bytes(s.TempWritten * 8192)}" : ""));
            sb.AppendLine("  ```sql");
            sb.AppendLine("  " + Clip(PerfRedactor.Sql(s.Query), 900).Replace("\n", "\n  "));
            sb.AppendLine("  ```");
            if (plans is not null && plans.TryGetValue("pgss:" + s.QueryId, out var plan))
            {
                sb.AppendLine($"  Plan: see `query-plans/pgss-{s.QueryId}.json`.");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## N+1 suspects");
        Table(sb, new[] { "Route", "Fingerprint", "Requests", "Executions", "Max per request", "Caller" },
            d.NPlusOne.Take(20).Select(x => new[]
            {
                $"`{Safe(x.Route)}`", $"`{x.Fingerprint}`", PerfFormat.Count(x.Requests), PerfFormat.Count(x.Executions),
                x.MaxPerRequest.ToString(C), x.Caller is null ? "–" : $"`{Safe(x.Caller)}`",
            }));

        sb.AppendLine("## Tables & indexes");
        var bloat = d.Bloat.ToDictionary(b => b.Name);
        Table(sb, new[] { "Table", "Rows", "Dead %", "Size", "Bloat est.", "Seq scans (period)", "Idx scans (period)", "Last autovacuum", "Last autoanalyze" },
            d.Tables.Take(25).Select(t =>
            {
                var delta = d.TableDeltas.FirstOrDefault(x => x.Name == t.Name);
                return new[]
                {
                    $"`{t.Name}`", PerfFormat.Count(t.Rows), Pct(t.Rows == 0 ? 0 : 100.0 * t.Dead / t.Rows), PerfFormat.Bytes(t.TotalBytes),
                    bloat.TryGetValue(t.Name, out var b) ? PerfFormat.Pct(b.BloatPct) : "–",
                    delta is { HasBaseline: true } ? PerfFormat.Count(delta.SeqScan) : "–",
                    delta is { HasBaseline: true } ? PerfFormat.Count(delta.IdxScan) : "–",
                    PerfFormat.Date(t.LastAutovacuum), PerfFormat.Date(t.LastAutoanalyze),
                };
            }));
        var unused = d.Indexes.Where(i => i.Scans == 0 && !i.IsPrimary && !i.IsUnique).Take(15).ToList();
        if (unused.Count > 0)
        {
            sb.AppendLine("Unused indexes (0 scans since stats reset): " +
                          string.Join(", ", unused.Select(i => $"`{i.Index}` ({PerfFormat.Bytes(i.Bytes)})")));
            sb.AppendLine();
        }
        if (d.DuplicateIndexes.Count > 0)
        {
            sb.AppendLine("Duplicate indexes: " + string.Join("; ", d.DuplicateIndexes.Select(x => $"`{x.Table}`: {string.Join(", ", x.Indexes)}")));
            sb.AppendLine();
        }

        sb.AppendLine("## PostgreSQL health");
        var db = d.PgDatabase;
        Table(sb, new[] { "Metric", "Value" }, new[]
        {
            new[] { "Buffer cache hit ratio (period)", db.CacheHitPct is { } hit ? PerfFormat.Pct(hit) : "–" },
            new[] { "Temp files / bytes (period)", $"{Num(db.Delta("temp_files"))} / {PerfFormat.Bytes((long)(db.Delta("temp_bytes") ?? 0))}" },
            new[] { "Deadlocks (period)", Num(db.Delta("deadlocks")) },
            new[] { "Rollbacks / commits (period)", $"{Num(db.Delta("xact_rollback"))} / {Num(db.Delta("xact_commit"))}" },
            new[] { "Checkpoints timed / requested", $"{Num(db.Delta("checkpoints_timed"))} / {Num(db.Delta("checkpoints_req"))}" },
            new[] { "Max idle-in-transaction", db.IdleInTxMaxS is { } i ? PerfFormat.Duration(i * 1000) : "–" },
            new[] { "Max blocked sessions", Num(db.BlockedMax) },
            new[] { "Max connections in use", Num(db.ConnTotalMax) },
            new[] { "Transaction ID age (wraparound)", Num(db.Latest("xid_age")) },
            new[] { "Database size", PerfFormat.Bytes((long)(db.Latest("db_size") ?? 0)) },
        });

        sb.AppendLine("## Server / runtime");
        Table(sb, new[] { "Metric", "Value" }, new[]
        {
            new[] { "Host CPU avg / max", $"{Pct(rt.HostCpuPct)} / {Pct(rt.HostCpuMax)}" },
            new[] { "CPU steal avg / max", $"{Pct(rt.StealPct)} / {Pct(rt.StealMax)}" },
            new[] { "CPU iowait avg", Pct(rt.IoWaitPct) },
            new[] { "Load avg / max", $"{Num(rt.Load1, "0.00")} / {Num(rt.Load1Max, "0.00")}" },
            new[] { "Min available RAM", $"{Num(rt.MemAvailableMinMb)} MB of {Num(rt.MemTotalMb)} MB" },
            new[] { "Swap activity (pages)", Num(rt.SwapActivity) },
            new[] { "Disk latency avg / max", $"{Num(rt.DiskAwaitMs, "0.0")} / {Num(rt.DiskAwaitMax, "0.0")} ms" },
            new[] { "Disk utilisation avg / max", $"{Pct(rt.DiskUtilAvg)} / {Pct(rt.DiskUtilMax)}" },
            new[] { "Container CPU throttled", Pct(rt.CgThrottledPct) },
            new[] { "App CPU avg / max", $"{Pct(rt.CpuPct)} / {Pct(rt.CpuPctMax)}" },
            new[] { "GC pause share", PerfFormat.Pct(rt.GcPausePct) },
            new[] { "Managed heap start → end (max)", $"{Num(rt.GcHeapMbStart)} → {Num(rt.GcHeapMbEnd)} MB ({Num(rt.GcHeapMbMax)} MB)" },
            new[] { "Thread-pool queue non-empty", PerfFormat.Pct(rt.ThreadPoolQueuePct) },
            new[] { "Thread-pool threads start → end (max)", $"{Num(rt.TpThreadsStart)} → {Num(rt.TpThreadsEnd)} ({rt.TpThreadsMax})" },
            new[] { "Exceptions (first-chance)", PerfFormat.Count(rt.Exceptions) },
            new[] { "DB pool: max busy / size, waits, timeouts", $"{rt.PoolBusyMax} / {rt.PoolMax}, {PerfFormat.Pct(rt.PoolWaitPct)} of samples, {rt.PoolTimeouts}" },
            new[] { "Max in-flight requests", rt.InFlightMax.ToString(C) },
        });

        // v0.1.25 — which exceptions (type names only; the throwing class is
        // added while Diagnose runs). Process-wide since app start, not
        // limited to the report period.
        var (exSince, exTypes) = RuntimeSampler.ExceptionTypes(15);
        if (exTypes.Count > 0)
        {
            sb.AppendLine($"First-chance exceptions by type since app start ({Local(exSince)}):").AppendLine();
            Table(sb, new[] { "Exception type", "Count" },
                exTypes.Select(kv => new[] { $"`{Safe(kv.Key)}`", PerfFormat.Count(kv.Value) }));
        }

        sb.AppendLine("## Frontend (real users)");
        var views = d.RumOf("view").ToDictionary(v => v.Route, v => v.Count);
        var vitalRoutes = d.Rum.Where(r => r.Metric is "lcp" or "inp" or "cls" or "ttfb" or "fcp").Select(r => r.Route).Distinct()
            .OrderByDescending(r => views.GetValueOrDefault(r)).Take(20).ToList();
        if (vitalRoutes.Count == 0)
        {
            sb.AppendLine("No real-user data in this period (Frontend collector off, or no sampled page loads).").AppendLine();
        }
        else
        {
            RumStats? Get(string route, string metric) => d.Rum.FirstOrDefault(x => x.Route == route && x.Metric == metric);
            Table(sb, new[] { "Route", "Views", "LCP p75", "INP p75", "CLS p75", "TTFB p75", "Long tasks", "API calls/screen p75", "API calls on page load p75" },
                vitalRoutes.Select(r => new[]
                {
                    $"`{Safe(r)}`", PerfFormat.Count(views.GetValueOrDefault(r)),
                    Get(r, "lcp") is { } lcp ? PerfFormat.Ms(lcp.P75) : "–",
                    Get(r, "inp") is { } inp ? PerfFormat.Ms(inp.P75) : "–",
                    Get(r, "cls") is { } cls ? (cls.P75 / 1000).ToString("0.00", C) : "–",
                    Get(r, "ttfb") is { } ttfb ? PerfFormat.Ms(ttfb.P75) : "–",
                    PerfFormat.Count(d.Rum.Where(x => x.Route == r && x.Metric == "long_task").Sum(x => x.Count)),
                    Get(r, "screen_api_calls") is { } calls ? calls.P75.ToString("0", C) : "–",
                    Get(r, "load_api_calls") is { } load ? load.P75.ToString("0", C) : "–",
                }));
            var apiTotal = d.RumOf("api_total").Sum(x => x.SumValue);
            var apiNet = d.RumOf("api_network").Sum(x => x.SumValue);
            if (apiTotal > 0) sb.AppendLine($"Network share of API time in the browser: {PerfFormat.Pct(100 * apiNet / apiTotal)}.").AppendLine();

            // v0.1.26 — which element moved most when a page jumped (CSS
            // selector of web-vitals' largest shift target; digits masked).
            var shifts = d.RumOf("cls_target").Where(x => x.Detail != "")
                .OrderByDescending(x => x.SumValue).Take(10).ToList();
            if (shifts.Count > 0)
            {
                sb.AppendLine("Layout shift sources (element that moved most, per page load):").AppendLine();
                Table(sb, new[] { "Route", "Element", "Page loads", "CLS max" },
                    shifts.Select(x => new[]
                    {
                        $"`{Safe(x.Route)}`", $"`{Safe(x.Detail)}`", PerfFormat.Count(x.Count),
                        (x.MaxValue / 1000).ToString("0.00", C),
                    }));
            }
        }
        if (d.Bundle is { } bundle)
        {
            sb.AppendLine($"Bundle: JS {PerfFormat.Bytes(bundle.TotalJsBytes)} ({PerfFormat.Bytes(bundle.TotalJsGzipBytes)} gzip), CSS {PerfFormat.Bytes(bundle.TotalCssBytes)}. Largest chunks: " +
                          string.Join(", ", bundle.Chunks.OrderByDescending(c => c.GzipBytes).Take(5).Select(c => $"`{Safe(c.Name)}` {PerfFormat.Bytes(c.GzipBytes)}")));
            sb.AppendLine();
        }

        sb.AppendLine("## External APIs and background jobs");
        Table(sb, new[] { "Host", "Calls", "Errors", "429", "p50", "p95" },
            d.SpansOf("external").GroupBy(s => s.Name).Select(g =>
            {
                var hist = PerfHistogram.Merge(g.Select(x => (IReadOnlyList<long>)x.Hist));
                var max = g.Max(x => x.MaxMs);
                return new[]
                {
                    $"`{Safe(g.Key)}`", PerfFormat.Count(g.Sum(x => x.Count)), PerfFormat.Count(g.Sum(x => x.Errors)),
                    PerfFormat.Count(g.Where(x => x.Detail.EndsWith("429", StringComparison.Ordinal)).Sum(x => x.Count)),
                    PerfFormat.Ms(PerfHistogram.Percentile(hist, 50, max)), PerfFormat.Ms(PerfHistogram.Percentile(hist, 95, max)),
                };
            }));
        Table(sb, new[] { "Worker", "Runs", "Failures", "Avg", "Max", "Total" },
            d.Workers.Take(30).Select(w => new[]
            {
                $"`{Safe(w.Worker)}`", PerfFormat.Count(w.Runs), PerfFormat.Count(w.Failures), PerfFormat.Ms(w.AvgMs), PerfFormat.Ms(w.MaxMs), PerfFormat.Duration(w.TotalMs),
            }));
        if (d.WorkerOverlaps.Count > 0)
        {
            Table(sb, new[] { "Worker", "API p95 during runs", "API p95 otherwise", "Overlapping minutes" },
                d.WorkerOverlaps.Select(o => new[] { $"`{Safe(o.Worker)}`", PerfFormat.Ms(o.P95During), PerfFormat.Ms(o.P95Outside), PerfFormat.Count(o.MinutesDuring) }));
        }

        if (d.VersionComparison is { } vc || d.PeriodComparison is not null)
        {
            sb.AppendLine("## Comparison");
            if (d.VersionComparison is { } v && v.Routes.Count > 0)
            {
                sb.AppendLine($"### Version {v.Before} → {v.After} (p95, biggest changes)");
                Table(sb, new[] { "Route", v.Before, v.After, "Change" },
                    v.Routes.Where(r => Math.Abs(r.ChangePct) >= 10).Take(20).Select(r => new[]
                    {
                        $"`{r.Method} {r.Route}`", PerfFormat.Ms(r.BeforeP95), PerfFormat.Ms(r.AfterP95), $"{r.ChangePct:+0;-0}%",
                    }));
            }
            if (d.PeriodComparison is { Count: > 0 } pc)
            {
                sb.AppendLine("### Previous period of equal length (p95, biggest changes)");
                Table(sb, new[] { "Route", "Before", "Now", "Change" },
                    pc.Where(r => Math.Abs(r.ChangePct) >= 10).Take(20).Select(r => new[]
                    {
                        $"`{r.Method} {r.Route}`", PerfFormat.Ms(r.BeforeP95), PerfFormat.Ms(r.AfterP95), $"{r.ChangePct:+0;-0}%",
                    }));
            }
        }

        if (d.Markers.Count > 0)
        {
            sb.AppendLine("## Markers in period");
            foreach (var m in d.Markers.Take(30))
            {
                sb.AppendLine($"- {Local(new DateTimeOffset(m.TsUtc, TimeSpan.Zero))} · {m.Kind} · {PerfRedactor.Text(m.Label, maskNumbers: true)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Missing data / limitations");
        var gaps = new List<string>();
        foreach (var (flag, label) in new[]
                 {
                     (PerfCollector.Http, "HTTP"), (PerfCollector.Database, "Database"), (PerfCollector.Postgres, "PostgreSQL statistics"),
                     (PerfCollector.Runtime, "Runtime"), (PerfCollector.Host, "Host"), (PerfCollector.Frontend, "Frontend/RUM"),
                     (PerfCollector.SignalR, "SignalR"), (PerfCollector.Workers, "Background workers"), (PerfCollector.External, "External HTTP"),
                 })
        {
            if ((d.Options.Collectors & flag) != flag || d.Options.BaseLevel == PerfLevel.Off) gaps.Add($"Collector '{label}' is switched off.");
        }
        if (!d.HostSupported) gaps.Add("Host metrics are only available on Linux.");
        if (d.TableDeltas.Count > 0 && d.TableDeltas.All(x => !x.HasBaseline))
            gaps.Add("No table-statistics snapshot at the start of this period (snapshots run hourly, every 15 min in Diagnose): per-table scans and writes for the period are not available, and the table-scan and vacuum findings were skipped.");
        if (d.PgAccess.StatStatementsReason is { } reason) gaps.Add(reason);
        if (d.Runtime.DiagnoseMinutes == 0) gaps.Add("Diagnose mode did not run in this period: no per-query fingerprints, N+1 detection or query callers.");
        gaps.AddRange(d.Gaps.Select(g => $"Section '{g}' could not be read."));
        if (gaps.Count == 0) gaps.Add("None.");
        foreach (var g in gaps) sb.AppendLine("- " + g);

        var markdown = sb.ToString();
        if (Encoding.UTF8.GetByteCount(markdown) > MaxMarkdownBytes)
        {
            // Keep the report under the size budget: cut at a section boundary.
            var cut = markdown[..Math.Min(markdown.Length, MaxMarkdownBytes - 400)];
            var lastSection = cut.LastIndexOf("\n## ", StringComparison.Ordinal);
            if (lastSection > 0) cut = cut[..lastSection];
            markdown = cut + "\n\n_(Report truncated to stay compact — the JSON file holds the full data.)_\n";
        }
        return markdown;
    }

    public string BuildJson(PerfDataset d, IReadOnlyList<PerfFinding> findings, IReadOnlyList<PerfCategoryScore> scores)
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            generatedUtc = d.GeneratedUtc,
            period = new { from = d.Period.From, to = d.Period.To, usesHourly = d.Period.UsesHourly },
            appVersion = d.AppVersion,
            environment = new
            {
                hostCores = d.Runtime.HostCores,
                memTotalMb = d.Runtime.MemTotalMb,
                postgres = d.PgAccess.ServerVersion,
                pgMonitor = d.PgAccess.PgMonitor,
                pgStatStatements = d.PgAccess.StatStatements,
                maxPoolSize = d.MaxPoolSize,
                dotnet = Environment.Version.ToString(),
                pgSettings = d.PgSettings.Select(s => new { s.Name, s.Setting, s.Unit }),
            },
            categories = scores,
            budgets = d.Budgets,
            findings = findings.Select(f => new
            {
                f.Key,
                f.Category,
                f.Severity,
                title = Safe(f.Title),
                explanation = Safe(f.Explanation),
                evidence = f.Evidence.Select(e => new { label = Safe(e.Label), value = Safe(e.Value) }),
                cause = Safe(f.Cause),
                action = Safe(f.Action),
                codeRefs = f.CodeRefs.Select(r => new { r.Kind, value = Safe(r.Value), sql = r.Sql is null ? null : PerfRedactor.Sql(r.Sql), caller = r.Caller is null ? null : Safe(r.Caller) }),
                f.ImpactMs,
            }),
            total = d.Total is null ? null : RouteDto(d.Total),
            routes = d.Routes.Select(RouteDto),
            timeline = d.Timeline.Select(p => new { t = p.T, p.Count, p.Errors, p50 = Round(p.P50), p95 = Round(p.P95), p99 = Round(p.P99), dbMs = Round(p.DbMs), extMs = Round(p.ExtMs), p.Users }),
            appQueries = d.Queries.Select(q => new { q.Fingerprint, sql = PerfRedactor.Sql(q.Sql), caller = q.Caller is null ? null : Safe(q.Caller), q.Count, q.Errors, totalMs = Round(q.SumMs), avgMs = Round(q.AvgMs), p95 = Round(q.P95), maxMs = Round(q.MaxMs), sources = q.Sources.Select(s => new { source = Safe(s.Source), s.Count, totalMs = Round(s.SumMs) }) }),
            pgStatements = d.PgStatements.Take(100).Select(s => new { s.QueryId, query = PerfRedactor.Sql(s.Query), s.Calls, totalMs = Round(s.TotalMs), meanMs = Round(s.MeanMs), s.Rows, hitPct = Round(s.HitPct), s.BlksRead, s.TempWritten }),
            nPlusOne = d.NPlusOne.Select(x => new { route = Safe(x.Route), x.Fingerprint, sql = x.Sql is null ? null : PerfRedactor.Sql(x.Sql), caller = x.Caller is null ? null : Safe(x.Caller), x.Requests, x.Executions, x.MaxPerRequest }),
            slowRequests = d.SlowRequests.Select(s => new { ts = s.TsUtc, s.Method, route = Safe(s.Route), s.Status, s.TotalMs, s.DbMs, s.DbCount, s.ExtMs, s.PipelineMs, s.GcCount, s.Bytes, breakdown = ParseJson(s.Breakdown) }),
            tables = d.Tables.Select(t => new { t.Name, t.Rows, t.Dead, t.TotalBytes, t.TableBytes, t.IndexBytes, t.SeqScan, t.IdxScan, t.ModSinceAnalyze, t.LastAutovacuum, t.LastAutoanalyze, t.LastVacuum, t.LastAnalyze }),
            tableDeltas = d.TableDeltas,
            bloat = d.Bloat,
            indexes = d.Indexes,
            duplicateIndexes = d.DuplicateIndexes,
            database = new { start = d.PgDatabase.Start, end = d.PgDatabase.End, cacheHitPct = d.PgDatabase.CacheHitPct, d.PgDatabase.IdleInTxMaxS, d.PgDatabase.BlockedMax, d.PgDatabase.LongestActiveMaxS },
            runtime = d.Runtime,
            spans = d.Spans.Select(s => new { s.Kind, name = Safe(s.Name), detail = Safe(s.Detail), s.Count, s.Errors, totalMs = Round(s.SumMs), p50 = Round(s.P50), p95 = Round(s.P95), maxMs = Round(s.MaxMs) }),
            rum = d.Rum.Select(r => new { route = Safe(r.Route), r.Metric, detail = Safe(r.Detail), r.Count, avg = Round(r.Avg), p75 = Round(r.P75), p95 = Round(r.P95), max = Round(r.MaxValue) }),
            rumBreakdown = d.RumBreakdown,
            workers = d.Workers,
            workerOverlaps = d.WorkerOverlaps,
            versions = d.Versions,
            versionComparison = d.VersionComparison,
            periodComparison = d.PeriodComparison,
            markers = d.Markers.Select(m => new { m.TsUtc, m.Kind, label = PerfRedactor.Text(m.Label, maskNumbers: true) }),
            bundle = d.Bundle,
            gaps = d.Gaps,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public byte[] BuildZip(string markdown, string json, IReadOnlyDictionary<string, string> plans)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "performance-report.md", markdown);
            Add(zip, "performance-data.json", json);
            foreach (var (key, plan) in plans)
            {
                var name = key.Replace(':', '-');
                Add(zip, $"query-plans/{name}.json", plan);
            }
        }
        return ms.ToArray();

        static void Add(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
    }

    private static object RouteDto(HttpRouteStats r) => new
    {
        r.Method,
        route = Safe(r.Route),
        r.Count,
        errors5xx = r.Errors,
        r.Count4xx,
        r.Count429,
        p50 = Round(r.P50),
        p95 = Round(r.P95),
        p99 = Round(r.P99),
        maxMs = Round(r.MaxMs),
        avgMs = Round(r.AvgMs),
        avgDbMs = Round(r.AvgDbMs),
        avgDbQueries = Round(r.AvgDbQueries),
        avgExtMs = Round(r.AvgExtMs),
        avgPipelineMs = Round(r.AvgPipelineMs),
        avgAppMs = Round(r.AvgAppMs),
        avgKb = Round(r.AvgKb),
        p95Kb = Round(r.P95Kb),
        r.Users,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return null; }
    }

    private static string TopQueries(string? breakdownJson)
    {
        if (string.IsNullOrEmpty(breakdownJson)) return "–";
        try
        {
            using var doc = JsonDocument.Parse(breakdownJson);
            if (!doc.RootElement.TryGetProperty("queries", out var queries)) return "–";
            return string.Join(", ", queries.EnumerateArray().Take(3).Select(q =>
                $"`{q.GetProperty("fingerprint").GetString()}`×{q.GetProperty("count").GetInt64()}"));
        }
        catch
        {
            return "–";
        }
    }

    private static void Table(StringBuilder sb, string[] headers, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            sb.AppendLine("_No data._").AppendLine();
            return;
        }
        sb.AppendLine("| " + string.Join(" | ", headers.Select(Cell)) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var r in list) sb.AppendLine("| " + string.Join(" | ", r.Select(Cell)) + " |");
        sb.AppendLine();

        static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    private static string Safe(string? s) => PerfRedactor.Text(s);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 2) : 0;

    private static string Num(double? v, string format = "0") => v is null ? "–" : v.Value.ToString(format, C);

    private static string Pct(double? v) => v is null ? "–" : PerfFormat.Pct(v.Value);

    private static TimeZoneInfo ResolveZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* fall through */ }
        }
        return TimeZoneInfo.Utc;
    }
}
