using System.Globalization;

namespace Servicedesk.Infrastructure.Performance;

public static class PerfCategory
{
    public const string Hosting = "hosting";
    public const string Network = "network";
    public const string Code = "code";
    public const string Database = "database";
    public const string Maintenance = "maintenance";
    public const string Frontend = "frontend";
    public const string Background = "background";

    public static readonly (string Key, string Label, string Question)[] All =
    {
        (Hosting, "Hosting / server", "Is the server (CPU, memory, disk) or the VPS provider the bottleneck?"),
        (Network, "Network / internet", "Is time lost between the browser and the server?"),
        (Code, "Backend code", "Is our own C# code, an external API or the request pipeline slow?"),
        (Database, "Database queries", "Which queries cost the most, and why?"),
        (Maintenance, "Database maintenance", "Is PostgreSQL cleaning up and caching well?"),
        (Frontend, "Frontend / browser", "Is the page slow to load or sluggish to use?"),
        (Background, "Background & realtime", "Do background jobs or realtime traffic slow users down?"),
    };
}

public static class PerfSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static int Rank(string severity) => severity switch { Critical => 3, Warning => 2, _ => 1 };
}

public sealed record PerfEvidence(string Label, string Value);

/// Where to look in the code: a route template, a query fingerprint (+ its
/// normalised SQL and calling method), a worker, a frontend route, a table.
public sealed record PerfCodeRef(string Kind, string Value, string? Sql = null, string? Caller = null);

public sealed record PerfFinding(
    string Key,
    string Category,
    string Severity,
    string Title,
    string Explanation,
    IReadOnlyList<PerfEvidence> Evidence,
    string Cause,
    string Action,
    IReadOnlyList<PerfCodeRef> CodeRefs,
    double ImpactMs)
{
    /// "Total extra waiting time" in a human unit, shown next to each finding.
    public string ImpactLabel => PerfFormat.Duration(ImpactMs);
}

public sealed record PerfCategoryScore(string Key, string Label, string Question, int Score, string Status, string Summary, int FindingCount);

/// Rule engine behind "Top bottlenecks". Plain, explainable rules (no ML):
/// each finding says which of the seven categories it belongs to, how bad it
/// is, the evidence, the likely cause, what to do and where in the code to
/// look. Ranking is by impact ≈ extra time per occurrence × occurrences, so
/// a 50 ms query that runs 100 000 times can outrank a 3 s one that ran twice.
public static class PerfFindings
{
    public static IReadOnlyList<PerfFinding> Evaluate(PerfDataset d)
    {
        // Titles/evidence interpolate numbers: always render them invariant
        // ("1.5 s", never "1,5 s"), whatever the server culture.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return EvaluateCore(d);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static IReadOnlyList<PerfFinding> EvaluateCore(PerfDataset d)
    {
        var f = new List<PerfFinding>();
        var t = d.Thresholds;

        RouteRules(d, t, f);
        BudgetRules(d, f);
        QueryRules(d, t, f);
        TableRules(d, t, f);
        PostgresRules(d, t, f);
        HostRules(d, t, f);
        RuntimeRules(d, t, f);
        NetworkRules(d, t, f);
        FrontendRules(d, t, f);
        BackgroundRules(d, t, f);
        RegressionRules(d, t, f);
        MonitorRules(d, f);

        return f
            .GroupBy(x => x.Key)
            .Select(g => g.First())
            .OrderByDescending(x => x.ImpactMs)
            .ThenByDescending(x => PerfSeverity.Rank(x.Severity))
            .ToList();
    }

    /// Health score per category: 100 minus a penalty per finding.
    public static IReadOnlyList<PerfCategoryScore> Score(PerfDataset d, IReadOnlyList<PerfFinding> findings)
    {
        var result = new List<PerfCategoryScore>();
        foreach (var (key, label, question) in PerfCategory.All)
        {
            var own = findings.Where(x => x.Category == key).ToList();
            var penalty = own.Sum(x => x.Severity switch
            {
                PerfSeverity.Critical => 40,
                PerfSeverity.Warning => 15,
                _ => 3,
            });
            var hasData = HasData(d, key);
            var score = hasData ? Math.Clamp(100 - penalty, 0, 100) : -1;
            var status = !hasData ? "nodata" : score >= 80 ? "good" : score >= 50 ? "warning" : "critical";
            var summary = !hasData
                ? "No data for this category in the selected period."
                : own.Count == 0
                    ? "No problems detected."
                    : own.OrderByDescending(x => PerfSeverity.Rank(x.Severity)).ThenByDescending(x => x.ImpactMs).First().Title;
            result.Add(new PerfCategoryScore(key, label, question, score, status, summary, own.Count));
        }
        return result;
    }

    private static bool HasData(PerfDataset d, string category) => category switch
    {
        PerfCategory.Hosting => d.Runtime.HostMinutes > 0 || d.Runtime.Minutes > 0,
        PerfCategory.Network => d.RumOf("api_total").Any(),
        PerfCategory.Code => (d.Total?.Count ?? 0) > 0,
        PerfCategory.Database => (d.Total?.Count ?? 0) > 0 || d.PgStatements.Count > 0,
        PerfCategory.Maintenance => d.Tables.Count > 0 || d.PgDatabase.End.Count > 0,
        PerfCategory.Frontend => d.Rum.Count > 0,
        PerfCategory.Background => d.Workers.Count > 0 || d.Spans.Count > 0,
        _ => false,
    };

    // ------------------------------------------------------------- routes

    private static void RouteRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        foreach (var r in d.ApiRoutes)
        {
            if (r.Count < t.RouteMinRequests) continue;

            // Payload size, independent of latency.
            if (r.P95Kb > t.ResponseKb)
            {
                f.Add(new PerfFinding(
                    "payload:" + r.Key, PerfCategory.Code, PerfSeverity.Warning,
                    $"{r.Key} returns large responses ({PerfFormat.Kb(r.P95Kb)} at p95)",
                    "Large JSON responses take time to serialise on the server, to transfer and to parse in the browser — a slow connection makes it much worse.",
                    new[]
                    {
                        new PerfEvidence("p95 response size", PerfFormat.Kb(r.P95Kb)),
                        new PerfEvidence("Average size", PerfFormat.Kb(r.AvgKb)),
                        new PerfEvidence("Requests", PerfFormat.Count(r.Count)),
                    },
                    "The endpoint returns more data than the screen needs (whole lists, full bodies, unused fields).",
                    "Page the result, return only the fields the screen shows, or load details on demand.",
                    new[] { new PerfCodeRef("route", r.Key) },
                    r.Count * (r.AvgKb / 10.0))); // ~10 MB/s effective transfer
            }

            if (r.P95 <= t.RouteP95Ms) continue;
            var severity = r.P95 > t.RouteP95Ms * 3 ? PerfSeverity.Critical : PerfSeverity.Warning;
            var evidence = new List<PerfEvidence>
            {
                new("p95", PerfFormat.Ms(r.P95)),
                new("p50", PerfFormat.Ms(r.P50)),
                new("Requests", PerfFormat.Count(r.Count)),
                new("Time split", $"database {r.DbSharePct:0}% · external {r.ExtSharePct:0}% · pipeline {r.PipelineSharePct:0}% · own code {r.AppSharePct:0}%"),
                new("Queries per request", r.AvgDbQueries.ToString("0.0", CultureInfo.InvariantCulture)),
            };
            var refs = new List<PerfCodeRef> { new("route", r.Key) };
            foreach (var q in d.Queries.Where(q => q.Sources.Any(s => s.Source == r.Key)).OrderByDescending(q =>
                         q.Sources.First(s => s.Source == r.Key).SumMs).Take(3))
            {
                refs.Add(new PerfCodeRef("fingerprint", q.Fingerprint, q.Sql, q.Caller));
            }

            if (r.DbSharePct > t.DbSharePct)
            {
                f.Add(new PerfFinding(
                    "route-db:" + r.Key, PerfCategory.Database, severity,
                    $"{r.Key} is slow because of database queries",
                    $"{r.DbSharePct:0}% of this endpoint's time is spent waiting on PostgreSQL ({r.AvgDbQueries:0.0} queries per request on average).",
                    evidence,
                    r.AvgDbQueries >= 10
                        ? "Many queries per request — likely an N+1 pattern or a loop that queries per item."
                        : "One or more queries are slow — missing index, large scan or heavy join.",
                    r.AvgDbQueries >= 10
                        ? "Batch the per-item queries into one (WHERE id = ANY(@ids)) or join them; see the queries listed below."
                        : "Look at the listed queries: add an index, narrow the query, or run 'Show plan' on the Database tab.",
                    refs,
                    r.DbMs));
            }
            else if (r.ExtSharePct > t.ExtSharePct)
            {
                f.Add(new PerfFinding(
                    "route-ext:" + r.Key, PerfCategory.Code, severity,
                    $"{r.Key} waits on an external API",
                    $"{r.ExtSharePct:0}% of this endpoint's time is spent in outgoing HTTP calls (Microsoft Graph, Adsolut, …) while the user waits.",
                    evidence,
                    "A user request calls an external service synchronously.",
                    "Cache the external answer, move the call to a background worker, or call it in parallel instead of one after another.",
                    refs,
                    r.ExtMs));
            }
            else if (r.PipelineSharePct > 50)
            {
                f.Add(new PerfFinding(
                    "route-pipeline:" + r.Key, PerfCategory.Code, severity,
                    $"{r.Key} loses its time before the handler runs",
                    "Most time is spent in the request pipeline (rate limiter, authentication/session check, CSRF, version gate) rather than in the endpoint itself.",
                    evidence,
                    "Session validation or another middleware is slow (often a database round trip per request).",
                    "Check the session-validation query and its cache; look for middleware doing I/O on every request.",
                    refs,
                    r.PipelineMs));
            }
            else if (r.AppSharePct > t.AppSharePct)
            {
                f.Add(new PerfFinding(
                    "route-app:" + r.Key, PerfCategory.Code, severity,
                    $"{r.Key} is slow in its own code",
                    $"{r.AppSharePct:0}% of this endpoint's time is spent in our C# code — not in the database, not in external calls.",
                    evidence,
                    "CPU-heavy work (serialisation, HTML sanitising, PDF/report building), blocking calls or lock contention inside the handler.",
                    "Profile the handler; cache computed results; move heavy work out of the request; check the Server tab for thread-pool starvation.",
                    refs,
                    r.Count * r.AvgAppMs));
            }
            else
            {
                f.Add(new PerfFinding(
                    "route-mixed:" + r.Key, PerfCategory.Code, PerfSeverity.Info,
                    $"{r.Key} is slow (p95 {PerfFormat.Ms(r.P95)})",
                    "The time is spread over database, own code and other work without a single dominant cause.",
                    evidence,
                    "Several smaller costs add up.",
                    "Start with the biggest share in the time split; check the slow-request traces for this route.",
                    refs,
                    Math.Max(0, r.SumMs - r.Count * Math.Min(r.AvgMs, t.RouteP95Ms / 4.0))));
            }
        }

        var total = d.Total;
        if (total is not null && total.Count > 100 && total.Count429 > total.Count * 0.01)
        {
            f.Add(new PerfFinding(
                "rate-limited", PerfCategory.Code, PerfSeverity.Warning,
                $"{PerfFormat.Count(total.Count429)} requests were rejected by the rate limiter",
                "Users hit the rate limit: their requests fail with 429 and screens may show errors or retry.",
                new[] { new PerfEvidence("429 responses", PerfFormat.Count(total.Count429)), new PerfEvidence("Share", PerfFormat.Pct(100.0 * total.Count429 / total.Count)) },
                "A client fires too many calls (polling, per-keystroke lookups) or many users share one IP.",
                "Check the API calls per screen on the Frontend tab and the rate-limit card on the Health page.",
                Array.Empty<PerfCodeRef>(),
                total.Count429 * 1000.0));
        }
    }

    private static void BudgetRules(PerfDataset d, List<PerfFinding> f)
    {
        foreach (var b in d.Budgets.Where(b => b.HasData && !b.Ok))
        {
            var route = d.Routes.FirstOrDefault(r => r.Method == b.Method && r.Route == b.Route);
            f.Add(new PerfFinding(
                "budget:" + b.Method + " " + b.Route, PerfCategory.Code,
                b.P95 > b.TargetMs * 2 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Budget exceeded: {b.Method} {b.Route} p95 {PerfFormat.Ms(b.P95)} (target {PerfFormat.Ms(b.TargetMs)})",
                "This is one of the key flows with an explicit performance budget (Settings → Performance).",
                new[]
                {
                    new PerfEvidence("p95", PerfFormat.Ms(b.P95)),
                    new PerfEvidence("Budget", PerfFormat.Ms(b.TargetMs)),
                    new PerfEvidence("Requests", PerfFormat.Count(b.Count)),
                },
                route is null ? "See the route details." : $"Time split: database {route.DbSharePct:0}%, external {route.ExtSharePct:0}%, own code {route.AppSharePct:0}%.",
                "Open the route on the API tab to see its queries and slow traces.",
                new[] { new PerfCodeRef("route", b.Method + " " + b.Route) },
                Math.Max(0, (b.P95 - b.TargetMs) * b.Count * 0.05)));
        }
    }

    // ------------------------------------------------------------ database

    private static void QueryRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        var byFingerprint = d.Queries.ToDictionary(q => q.Fingerprint);
        foreach (var n in d.NPlusOne.Where(n => n.Requests >= t.NPlusOneMinRequests))
        {
            var avg = byFingerprint.TryGetValue(n.Fingerprint, out var q) ? q.AvgMs : 1;
            f.Add(new PerfFinding(
                "n1:" + n.Route + ":" + n.Fingerprint, PerfCategory.Database,
                n.MaxPerRequest >= 50 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"N+1 queries in {n.Route}",
                $"The same query runs up to {n.MaxPerRequest}× inside a single request — once per item instead of once for all items.",
                new[]
                {
                    new PerfEvidence("Requests with the pattern", PerfFormat.Count(n.Requests)),
                    new PerfEvidence("Executions", PerfFormat.Count(n.Executions)),
                    new PerfEvidence("Max per request", n.MaxPerRequest.ToString(CultureInfo.InvariantCulture)),
                    new PerfEvidence("Avg per execution", PerfFormat.Ms(avg)),
                },
                "A loop over a list calls the repository per element.",
                "Load all items in one query (WHERE x = ANY(@ids)) or join; then map in memory.",
                new[] { new PerfCodeRef("route", n.Route), new PerfCodeRef("fingerprint", n.Fingerprint, n.Sql, n.Caller) },
                Math.Max(0, (n.Executions - n.Requests) * Math.Max(avg, 0.2))));
        }

        foreach (var q in d.Queries.Where(q => q.Count >= 5 && q.AvgMs > t.SlowQueryMs))
        {
            f.Add(new PerfFinding(
                "query-slow:" + q.Fingerprint, PerfCategory.Database,
                q.AvgMs > t.SlowQueryMs * 5 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Slow query ({PerfFormat.Ms(q.AvgMs)} on average){(q.Caller is null ? "" : " in " + q.Caller)}",
                "This query shape is slower than the slow-query threshold on average.",
                new[]
                {
                    new PerfEvidence("Executions", PerfFormat.Count(q.Count)),
                    new PerfEvidence("Average", PerfFormat.Ms(q.AvgMs)),
                    new PerfEvidence("p95", PerfFormat.Ms(q.P95)),
                    new PerfEvidence("Total", PerfFormat.Duration(q.SumMs)),
                },
                "Missing or unusable index, a large scan, an expensive sort/join, or lock waits.",
                "Run 'Show plan' on the Database tab and look for Seq Scan / Sort / Nested Loop on big tables.",
                new[] { new PerfCodeRef("fingerprint", q.Fingerprint, q.Sql, q.Caller) },
                q.SumMs - q.Count * Math.Min(q.AvgMs, t.SlowQueryMs / 4.0)));
        }

        var pg = d.PgStatements;
        foreach (var s in pg.Where(s => s.Calls >= 5 && s.MeanMs > t.SlowQueryMs).Take(10))
        {
            f.Add(new PerfFinding(
                "pgss-slow:" + s.QueryId, PerfCategory.Database,
                s.MeanMs > t.SlowQueryMs * 5 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Slow statement in PostgreSQL ({PerfFormat.Ms(s.MeanMs)} mean)",
                "PostgreSQL's own statistics (pg_stat_statements) show this statement is slow — this also covers background workers and anything the app-side capture missed.",
                new[]
                {
                    new PerfEvidence("Calls", PerfFormat.Count(s.Calls)),
                    new PerfEvidence("Mean", PerfFormat.Ms(s.MeanMs)),
                    new PerfEvidence("Total", PerfFormat.Duration(s.TotalMs)),
                    new PerfEvidence("Buffer hit ratio", PerfFormat.Pct(s.HitPct)),
                },
                s.HitPct < 90 ? "It reads many pages from disk instead of memory." : "Expensive plan: scan, sort or join.",
                "Run 'Show plan' for it on the Database tab; check its indexes.",
                new[] { new PerfCodeRef("pg_statement", s.QueryId.ToString(CultureInfo.InvariantCulture), s.Query) },
                s.TotalMs - s.Calls * Math.Min(s.MeanMs, t.SlowQueryMs / 4.0)));
        }

        foreach (var s in pg.Where(s => s.TempWritten > 1000).OrderByDescending(s => s.TempWritten).Take(3))
        {
            f.Add(new PerfFinding(
                "pgss-temp:" + s.QueryId, PerfCategory.Database, PerfSeverity.Warning,
                $"A statement spills to disk ({PerfFormat.Bytes(s.TempWritten * 8192)} temp)",
                "Sorting or hashing did not fit in work_mem, so PostgreSQL wrote temporary files.",
                new[] { new PerfEvidence("Temp written", PerfFormat.Bytes(s.TempWritten * 8192)), new PerfEvidence("Calls", PerfFormat.Count(s.Calls)) },
                "The query sorts/hashes many rows, or work_mem is small.",
                "Narrow the query (filter earlier, LIMIT), add an index matching the ORDER BY, or raise work_mem carefully.",
                new[] { new PerfCodeRef("pg_statement", s.QueryId.ToString(CultureInfo.InvariantCulture), s.Query) },
                s.TotalMs * 0.2));
        }

        var top = (pg.Count > 0
                ? pg.Take(t.SlowQueryTopN).Select(s => (Ref: new PerfCodeRef("pg_statement", s.QueryId.ToString(CultureInfo.InvariantCulture), s.Query), s.TotalMs, Calls: s.Calls))
                : d.Queries.Take(t.SlowQueryTopN).Select(q => (Ref: new PerfCodeRef("fingerprint", q.Fingerprint, q.Sql, q.Caller), TotalMs: q.SumMs, Calls: q.Count)))
            .ToList();
        if (top.Count > 0 && top[0].TotalMs > 60_000)
        {
            f.Add(new PerfFinding(
                "query-top", PerfCategory.Database, PerfSeverity.Info,
                $"Heaviest queries by total time (top {top.Count})",
                "Not necessarily slow per call — these are the queries the database spends the most time on overall. Making a frequent query a little faster often helps more than fixing a rare slow one.",
                top.Select((x, i) => new PerfEvidence($"#{i + 1}", $"{PerfFormat.Duration(x.TotalMs)} over {PerfFormat.Count(x.Calls)} calls")).ToList(),
                "High frequency × moderate cost.",
                "Check whether results can be cached or the call frequency reduced.",
                top.Select(x => x.Ref).ToList(),
                top.Sum(x => x.TotalMs) * 0.05));
        }
    }

    private static void TableRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        foreach (var tb in d.TableDeltas.Where(x => x.Rows >= t.SeqScanMinRows && x.SeqScan >= 10 && x.SeqScan > Math.Max(1, x.IdxScan) * t.SeqScanRatio))
        {
            f.Add(new PerfFinding(
                "seqscan:" + tb.Name, PerfCategory.Database, PerfSeverity.Warning,
                $"Table {tb.Name} is read with full scans",
                $"{PerfFormat.Count(tb.SeqScan)} sequential scans vs {PerfFormat.Count(tb.IdxScan)} index scans in this period on a table with {PerfFormat.Count(tb.Rows)} rows.",
                new[]
                {
                    new PerfEvidence("Seq scans", PerfFormat.Count(tb.SeqScan)),
                    new PerfEvidence("Rows read by seq scans", PerfFormat.Count(tb.SeqTupRead)),
                    new PerfEvidence("Index scans", PerfFormat.Count(tb.IdxScan)),
                },
                "A frequent query filters on a column without a usable index.",
                "Find the queries touching this table on the Database tab and add an index on their WHERE/JOIN columns.",
                new[] { new PerfCodeRef("table", tb.Name) },
                tb.SeqTupRead * 0.0001));
        }

        var tables = d.Tables.ToDictionary(x => x.Name);
        foreach (var tb in d.Tables.Where(x => x.Rows + x.Dead > 1000 && x.Dead > 1000 && 100.0 * x.Dead / Math.Max(1, x.Rows) > t.DeadTuplePct))
        {
            f.Add(new PerfFinding(
                "dead:" + tb.Name, PerfCategory.Maintenance,
                100.0 * tb.Dead / Math.Max(1, tb.Rows) > t.DeadTuplePct * 3 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Table {tb.Name} has many dead rows ({PerfFormat.Pct(100.0 * tb.Dead / Math.Max(1, tb.Rows))})",
                "Updated and deleted rows stay behind as dead rows until VACUUM cleans them. They make the table and its indexes bigger, so every scan reads more pages.",
                new[]
                {
                    new PerfEvidence("Dead rows", PerfFormat.Count(tb.Dead)),
                    new PerfEvidence("Live rows", PerfFormat.Count(tb.Rows)),
                    new PerfEvidence("Last autovacuum", PerfFormat.Date(tb.LastAutovacuum)),
                },
                "Autovacuum cannot keep up (heavy write churn) or is blocked by a long transaction.",
                $"Run VACUUM (ANALYZE) {tb.Name} during a quiet moment; consider a lower autovacuum_vacuum_scale_factor for this table; check for 'idle in transaction' sessions.",
                new[] { new PerfCodeRef("table", tb.Name) },
                tb.Dead * 0.001));
        }

        foreach (var b in d.Bloat.Where(b => b.BloatPct > 30 && b.BloatBytes > 50 * 1024 * 1024))
        {
            f.Add(new PerfFinding(
                "bloat:" + b.Name, PerfCategory.Maintenance, PerfSeverity.Warning,
                $"Table {b.Name} is bloated (~{PerfFormat.Pct(b.BloatPct)} wasted)",
                "The table occupies much more space than its rows need (estimate). Scans read the empty space too.",
                new[] { new PerfEvidence("Estimated waste", PerfFormat.Bytes((long)b.BloatBytes)), new PerfEvidence("Table size", PerfFormat.Bytes((long)b.RealBytes)) },
                "Past bursts of updates/deletes; space is reusable but not returned.",
                "Usually fine to leave if it stays stable. To reclaim: pg_repack (online) or VACUUM FULL in a maintenance window (locks the table).",
                new[] { new PerfCodeRef("table", b.Name) },
                b.BloatBytes / 1024.0 / 1024.0 * 10));
        }

        var now = d.GeneratedUtc.UtcDateTime;
        foreach (var delta in d.TableDeltas.Where(x => x.Writes > 1000))
        {
            if (!tables.TryGetValue(delta.Name, out var tb)) continue;
            var last = Max(tb.LastVacuum, tb.LastAutovacuum);
            if (last is not null && (now - last.Value).TotalDays <= t.VacuumStaleDays) continue;
            f.Add(new PerfFinding(
                "vacuum-stale:" + tb.Name, PerfCategory.Maintenance, PerfSeverity.Warning,
                $"Table {tb.Name} is written to but was not vacuumed for {(last is null ? "a long time" : $"{(now - last.Value).TotalDays:0} days")}",
                "Vacuum keeps tables compact and the visibility map fresh (index-only scans depend on it).",
                new[] { new PerfEvidence("Writes in period", PerfFormat.Count(delta.Writes)), new PerfEvidence("Last vacuum", PerfFormat.Date(last)) },
                "Autovacuum thresholds are not reached, autovacuum is off, or it keeps getting cancelled.",
                $"Run VACUUM (ANALYZE) {tb.Name}; check the autovacuum settings on the Database → Config tab.",
                new[] { new PerfCodeRef("table", tb.Name) },
                delta.Writes * 0.01));
        }

        foreach (var tb in d.Tables.Where(x => x.Rows > 10_000 && x.ModSinceAnalyze > x.Rows * 0.2))
        {
            f.Add(new PerfFinding(
                "analyze:" + tb.Name, PerfCategory.Maintenance, PerfSeverity.Info,
                $"Statistics of {tb.Name} are out of date",
                "The planner chooses query plans from table statistics; after many changes it may pick bad plans.",
                new[] { new PerfEvidence("Rows changed since last ANALYZE", PerfFormat.Count(tb.ModSinceAnalyze)), new PerfEvidence("Rows", PerfFormat.Count(tb.Rows)) },
                "Many rows changed since the last (auto)analyze.",
                $"Use 'Analyze' on the Database → Tables tab for {tb.Name} (cheap and safe).",
                new[] { new PerfCodeRef("table", tb.Name) },
                tb.ModSinceAnalyze * 0.0005));
        }

        foreach (var ix in d.Indexes.Where(x => x.Scans == 0 && !x.IsUnique && !x.IsPrimary && x.Bytes > 10 * 1024 * 1024).Take(10))
        {
            f.Add(new PerfFinding(
                "unused-index:" + ix.Index, PerfCategory.Maintenance, PerfSeverity.Info,
                $"Index {ix.Index} is never used",
                "An unused index still slows down every insert/update on its table and takes memory.",
                new[] { new PerfEvidence("Size", PerfFormat.Bytes(ix.Bytes)), new PerfEvidence("Table", ix.Table) },
                "Created for a query that no longer exists, or superseded by another index. (Counters reset on stats reset — check it stayed 0 over weeks.)",
                "If it stays unused across a full business cycle, drop it in the schema bootstrap.",
                new[] { new PerfCodeRef("index", ix.Index), new PerfCodeRef("table", ix.Table) },
                ix.Bytes / 1024.0 / 1024.0));
        }

        foreach (var dup in d.DuplicateIndexes)
        {
            f.Add(new PerfFinding(
                "dup-index:" + string.Join(",", dup.Indexes), PerfCategory.Maintenance, PerfSeverity.Info,
                $"Duplicate indexes on {dup.Table}",
                "Two or more indexes have exactly the same definition; only one is needed.",
                new[] { new PerfEvidence("Indexes", string.Join(", ", dup.Indexes)), new PerfEvidence("Combined size", PerfFormat.Bytes(dup.Bytes)) },
                "An index was created twice under different names.",
                "Drop the redundant one in the schema bootstrap.",
                dup.Indexes.Select(i => new PerfCodeRef("index", i)).ToList(),
                dup.Bytes / 1024.0 / 1024.0));
        }
    }

    private static void PostgresRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        var db = d.PgDatabase;
        if (db.CacheHitPct is { } hit && hit < t.CacheHitPct)
        {
            f.Add(new PerfFinding(
                "cache-hit", PerfCategory.Maintenance, hit < t.CacheHitPct - 9 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"PostgreSQL reads from disk too often (cache hit {PerfFormat.Pct(hit)})",
                "Reading a page from RAM takes microseconds; from disk it can take milliseconds. Below ~99% the database waits on the disk noticeably.",
                new[] { new PerfEvidence("Buffer cache hit ratio", PerfFormat.Pct(hit)), new PerfEvidence("Blocks read from disk", PerfFormat.Count((long)(db.Delta("blks_read") ?? 0))) },
                "shared_buffers / RAM too small for the working set, bloated tables, or big scans pushing hot data out.",
                "Check shared_buffers on the Config tab vs the server RAM; fix full-table scans and bloat first.",
                Array.Empty<PerfCodeRef>(),
                (db.Delta("blks_read") ?? 0) * 0.1));
        }

        var tempBytes = db.Delta("temp_bytes") ?? 0;
        if (tempBytes > t.TempMb * 1024.0 * 1024.0)
        {
            f.Add(new PerfFinding(
                "temp-files", PerfCategory.Database, PerfSeverity.Warning,
                $"Queries spilled {PerfFormat.Bytes((long)tempBytes)} to temporary files",
                "Sorts and hashes that do not fit in work_mem are written to disk, which is much slower.",
                new[] { new PerfEvidence("Temp files", PerfFormat.Count((long)(db.Delta("temp_files") ?? 0))), new PerfEvidence("Temp bytes", PerfFormat.Bytes((long)tempBytes)) },
                "Large sorts/joins (reports, exports, big lists) or a small work_mem.",
                "Find the statements with temp usage on the Database tab; narrow them or raise work_mem carefully.",
                Array.Empty<PerfCodeRef>(),
                tempBytes / 1024.0 / 1024.0 * 5));
        }

        if (db.IdleInTxMaxS is { } idle && idle > t.IdleInTxSeconds)
        {
            f.Add(new PerfFinding(
                "idle-in-tx", PerfCategory.Code, idle > t.IdleInTxSeconds * 10 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"A session stayed 'idle in transaction' for {PerfFormat.Duration(idle * 1000)}",
                "A transaction that is open but doing nothing keeps its locks and stops VACUUM from cleaning up rows.",
                new[] { new PerfEvidence("Longest idle-in-transaction", PerfFormat.Duration(idle * 1000)) },
                "Code that opens a transaction and then awaits something slow (HTTP call, user input) before committing — or a manual psql session left open.",
                "Keep transactions short: do external calls before BEGIN or after COMMIT. Consider idle_in_transaction_session_timeout.",
                Array.Empty<PerfCodeRef>(),
                idle * 100));
        }

        if (db.BlockedMax is > 0 || (db.Delta("deadlocks") ?? 0) > 0)
        {
            var deadlocks = db.Delta("deadlocks") ?? 0;
            f.Add(new PerfFinding(
                "locks", PerfCategory.Code, deadlocks > 0 ? PerfSeverity.Warning : PerfSeverity.Info,
                deadlocks > 0 ? $"{deadlocks:0} deadlock(s) occurred" : "Sessions had to wait for locks",
                "When one transaction holds a lock another needs, the second waits — users see 'everything hangs for a moment'. A deadlock is aborted by PostgreSQL.",
                new[] { new PerfEvidence("Deadlocks", deadlocks.ToString("0", CultureInfo.InvariantCulture)), new PerfEvidence("Max blocked sessions in a snapshot", (db.BlockedMax ?? 0).ToString("0", CultureInfo.InvariantCulture)) },
                "Two code paths update the same rows in a different order, or a long transaction holds row locks.",
                "Look at the Live tab during the slowness; keep transactions short and lock rows in a consistent order.",
                Array.Empty<PerfCodeRef>(),
                deadlocks * 5000 + (db.BlockedMax ?? 0) * 500));
        }

        if (db.Latest("xid_age") is { } xid && xid > 1_000_000_000)
        {
            f.Add(new PerfFinding(
                "wraparound", PerfCategory.Maintenance, xid > 1_500_000_000 ? PerfSeverity.Critical : PerfSeverity.Warning,
                "Transaction-ID wraparound risk",
                "PostgreSQL must freeze old rows before transaction IDs run out (~2.1 billion). If it cannot, the database stops accepting writes to protect itself.",
                new[] { new PerfEvidence("Oldest unfrozen XID age", PerfFormat.Count((long)xid)) },
                "Autovacuum freezing is blocked or too slow (long transactions, disabled autovacuum).",
                "Run VACUUM (FREEZE) on the oldest tables soon; check for long-running transactions.",
                Array.Empty<PerfCodeRef>(),
                10_000_000));
        }

        if (d.Runtime.PoolTimeouts > 0 || d.Runtime.PoolWaitPct > t.PoolWaitPct)
        {
            f.Add(new PerfFinding(
                "pool-wait", PerfCategory.Code, d.Runtime.PoolTimeouts > 0 ? PerfSeverity.Critical : PerfSeverity.Warning,
                d.Runtime.PoolTimeouts > 0
                    ? $"The database connection pool ran out ({d.Runtime.PoolTimeouts} timeouts)"
                    : "Requests waited for a free database connection",
                $"The app keeps at most {d.Runtime.PoolMax} connections open. When all are busy, the next query waits — this shows up as 'suddenly everything is slow'.",
                new[]
                {
                    new PerfEvidence("Samples with waiting requests", PerfFormat.Pct(d.Runtime.PoolWaitPct)),
                    new PerfEvidence("Max waiting", d.Runtime.PoolPendingMax.ToString(CultureInfo.InvariantCulture)),
                    new PerfEvidence("Max busy connections", $"{d.Runtime.PoolBusyMax} of {d.Runtime.PoolMax}"),
                },
                "Connections are held too long (slow queries, external calls inside an open connection, leaks) or the pool is too small for the load.",
                "Fix slow queries first; never await HTTP calls while holding a connection; raise Database:MaxPoolSize only if PostgreSQL max_connections allows it.",
                Array.Empty<PerfCodeRef>(),
                d.Runtime.PoolTimeouts * 15_000 + d.Runtime.PoolWaitSamples * 1000));
        }
    }

    // ---------------------------------------------------------------- host

    private static void HostRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        var rt = d.Runtime;
        var apiMs = d.Total?.SumMs ?? 0;

        if (rt.StealPct is { } steal && steal > t.CpuStealPct)
        {
            f.Add(new PerfFinding(
                "cpu-steal", PerfCategory.Hosting, steal > t.CpuStealPct * 2 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"The VPS gets less CPU than promised (steal {PerfFormat.Pct(steal)})",
                "CPU steal is time your virtual server wanted to run but the hosting provider gave the physical CPU to another customer ('noisy neighbour'). Nothing in our code can fix that.",
                new[] { new PerfEvidence("Average steal", PerfFormat.Pct(steal)), new PerfEvidence("Peak steal", PerfFormat.Pct(rt.StealMax ?? steal)) },
                "Overbooked hypervisor at the hosting provider.",
                "Ask the provider, move to a dedicated-CPU plan, or migrate to another host. This is a hosting problem, not a code problem.",
                new[] { new PerfCodeRef("host", "cpu-steal") },
                apiMs * steal / 100));
        }

        if (rt.HostMinutes > 0 && rt.HostCpuHighMinutes > rt.HostMinutes * 0.2)
        {
            var appDominant = rt.CpuPct is > 60;
            f.Add(new PerfFinding(
                "cpu-high", appDominant ? PerfCategory.Code : PerfCategory.Hosting, PerfSeverity.Warning,
                $"Host CPU above {t.CpuPct}% for {PerfFormat.Pct(100.0 * rt.HostCpuHighMinutes / rt.HostMinutes)} of the time",
                appDominant
                    ? "The CPU is busy and most of it is the app itself."
                    : "The CPU is busy, mostly outside the app process (PostgreSQL, other services).",
                new[]
                {
                    new PerfEvidence("Host CPU avg / max", $"{PerfFormat.Pct(rt.HostCpuPct ?? 0)} / {PerfFormat.Pct(rt.HostCpuMax ?? 0)}"),
                    new PerfEvidence("App process CPU avg", PerfFormat.Pct(rt.CpuPct ?? 0)),
                },
                appDominant ? "CPU-heavy code paths or a busy background worker." : "Heavy queries in PostgreSQL or another process on the server.",
                appDominant ? "Check the slowest routes' own-code share and the worker timeline." : "Check the heaviest queries; consider a bigger VPS.",
                Array.Empty<PerfCodeRef>(),
                apiMs * 0.2));
        }

        if (rt.HostMinutes > 0 && rt.LoadHighMinutes > rt.HostMinutes * 0.2)
        {
            f.Add(new PerfFinding(
                "load-high", PerfCategory.Hosting, PerfSeverity.Warning,
                $"Server load above {t.LoadPctOfCores / 100.0:0.#}× the core count",
                "The load average counts processes running or waiting for CPU/disk. Far above the core count means work queues up.",
                new[] { new PerfEvidence("Load avg / max", $"{rt.Load1 ?? 0:0.0} / {rt.Load1Max ?? 0:0.0}"), new PerfEvidence("Cores", (rt.HostCores ?? 0).ToString(CultureInfo.InvariantCulture)) },
                "Too little CPU, or processes waiting on a slow disk.",
                "Compare with CPU and disk latency on the Server tab; scale the VPS if both are high.",
                Array.Empty<PerfCodeRef>(),
                apiMs * 0.1));
        }

        if (rt.MemTotalMb is > 0 && rt.MemAvailableMinMb is { } avail)
        {
            var pct = 100 * avail / rt.MemTotalMb.Value;
            var swapping = rt.SwapActivity is > 100;
            if (pct < t.MemAvailablePct || swapping)
            {
                f.Add(new PerfFinding(
                    "memory", PerfCategory.Hosting, swapping ? PerfSeverity.Critical : PerfSeverity.Warning,
                    swapping ? "The server is swapping" : $"Available memory dropped to {PerfFormat.Pct(pct)}",
                    "When RAM runs out the OS moves memory to disk (swap) — everything that touches swapped memory becomes very slow. PostgreSQL also caches less.",
                    new[]
                    {
                        new PerfEvidence("Min available", $"{avail:0} MB of {rt.MemTotalMb:0} MB"),
                        new PerfEvidence("Swap used (max)", $"{rt.SwapUsedMaxMb ?? 0:0} MB"),
                        new PerfEvidence("Swap pages in/out", PerfFormat.Count((long)(rt.SwapActivity ?? 0))),
                    },
                    "Too little RAM for PostgreSQL + the app + the OS cache, or a memory leak.",
                    "Add RAM, or lower shared_buffers/work_mem; check the app's heap trend for a leak.",
                    Array.Empty<PerfCodeRef>(),
                    apiMs * (swapping ? 0.3 : 0.1)));
            }
        }

        if ((rt.DiskAwaitMs is { } wait && wait > t.DiskAwaitMs) || (rt.DiskUtilAvg is { } util && util > t.DiskUtilPct))
        {
            f.Add(new PerfFinding(
                "disk", PerfCategory.Hosting, PerfSeverity.Warning,
                "The disk is slow or saturated",
                "Every query that misses the cache, every WAL write and every attachment goes to disk. A slow disk slows all of them.",
                new[]
                {
                    new PerfEvidence("Avg I/O latency (max)", $"{PerfFormat.Ms(rt.DiskAwaitMs ?? 0)} ({PerfFormat.Ms(rt.DiskAwaitMax ?? 0)})"),
                    new PerfEvidence("Avg / max utilisation", $"{PerfFormat.Pct(rt.DiskUtilAvg ?? 0)} / {PerfFormat.Pct(rt.DiskUtilMax ?? 0)}"),
                    new PerfEvidence("CPU iowait", PerfFormat.Pct(rt.IoWaitPct ?? 0)),
                },
                "Shared/network storage at the provider, or heavy I/O (backups, big scans, vacuum).",
                "Check whether it coincides with backups or workers; ask the provider for SSD/NVMe storage; run the benchmark to compare hosts.",
                new[] { new PerfCodeRef("host", "disk") },
                apiMs * 0.15));
        }

        foreach (var (label, pct, gb) in new[] { ("root filesystem", rt.RootFreePct, rt.RootFreeGb), ("blob storage", rt.BlobFreePct, rt.BlobFreeGb) })
        {
            if (pct is not { } free || free >= t.DiskFreePct) continue;
            f.Add(new PerfFinding(
                "disk-free:" + label, PerfCategory.Hosting, free < t.DiskFreePct / 2.0 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Low free space on the {label} ({PerfFormat.Pct(free)})",
                "A full disk stops PostgreSQL and attachment storage outright.",
                new[] { new PerfEvidence("Free", $"{PerfFormat.Pct(free)} ({gb ?? 0:0.0} GB)") },
                "Growth of data, logs, backups or Docker images.",
                "Free up space (old Docker images: docker image prune; old backups) or grow the disk.",
                new[] { new PerfCodeRef("host", "disk-free") },
                5_000_000));
        }

        if (rt.CgThrottledPct is { } throttled && throttled > 5)
        {
            f.Add(new PerfFinding(
                "cgroup-throttle", PerfCategory.Hosting, PerfSeverity.Warning,
                $"The app container hits its CPU limit ({PerfFormat.Pct(throttled)} of periods throttled)",
                "Docker limits how much CPU the container may use; when it hits the limit the app is paused briefly.",
                new[] { new PerfEvidence("Throttled periods", PerfFormat.Pct(throttled)) },
                "A CPU limit in docker-compose that is too low for the load.",
                "Raise or remove the container CPU limit.",
                Array.Empty<PerfCodeRef>(),
                apiMs * throttled / 100));
        }

        if (rt.OomKillsDelta is > 0)
        {
            f.Add(new PerfFinding(
                "oom", PerfCategory.Hosting, PerfSeverity.Critical,
                "The app container was killed for using too much memory",
                "The kernel's out-of-memory killer stopped a process in the container — the app restarts and users lose their work in progress.",
                new[] { new PerfEvidence("OOM kills", rt.OomKillsDelta.Value.ToString(CultureInfo.InvariantCulture)) },
                "Container memory limit too low, or a memory leak.",
                "Raise the memory limit; check the heap trend on the Server tab.",
                Array.Empty<PerfCodeRef>(),
                rt.OomKillsDelta.Value * 1_000_000));
        }

        if (rt.KestrelQueuedMax > 0)
        {
            f.Add(new PerfFinding(
                "kestrel-queue", PerfCategory.Hosting, PerfSeverity.Info,
                "Incoming connections had to queue",
                "The web server could not accept new connections immediately.",
                new[] { new PerfEvidence("Max queued connections", rt.KestrelQueuedMax.ToString(CultureInfo.InvariantCulture)) },
                "CPU saturation or thread-pool starvation.",
                "Check CPU and the thread-pool queue on the Server tab.",
                Array.Empty<PerfCodeRef>(),
                rt.KestrelQueuedMax * 1000));
        }
    }

    private static void RuntimeRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        var rt = d.Runtime;
        var apiMs = d.Total?.SumMs ?? 0;
        var threadsGrowing = rt.TpThreadsEnd is { } end && rt.TpThreadsStart is { } start && end > start * 1.2;
        if (rt.Samples > 30 && rt.ThreadPoolQueuePct > t.ThreadPoolQueuePct && (threadsGrowing || rt.TpThreadsMax > Environment.ProcessorCount * 8))
        {
            f.Add(new PerfFinding(
                "threadpool", PerfCategory.Code, PerfSeverity.Warning,
                "Thread-pool starvation (blocking code)",
                "Work items waited in the .NET thread-pool queue while the pool kept adding threads — the classic sign of code that blocks a thread (.Result, .Wait(), synchronous I/O) instead of awaiting.",
                new[]
                {
                    new PerfEvidence("Samples with a queue", PerfFormat.Pct(rt.ThreadPoolQueuePct)),
                    new PerfEvidence("Threads start → end (max)", $"{rt.TpThreadsStart ?? 0:0} → {rt.TpThreadsEnd ?? 0:0} ({rt.TpThreadsMax})"),
                },
                "Sync-over-async or blocking I/O somewhere in a hot path.",
                "Search for .Result / .Wait() / GetAwaiter().GetResult() and synchronous File/HTTP calls in request and worker code.",
                Array.Empty<PerfCodeRef>(),
                apiMs * 0.2));
        }

        if (rt.IntervalMs > 0 && rt.GcPausePct > t.GcPausePct)
        {
            f.Add(new PerfFinding(
                "gc-pause", PerfCategory.Code, PerfSeverity.Warning,
                $"The app spends {PerfFormat.Pct(rt.GcPausePct)} of its time paused for garbage collection",
                "During a GC pause no request makes progress.",
                new[] { new PerfEvidence("GC pause share", PerfFormat.Pct(rt.GcPausePct)), new PerfEvidence("Gen2 collections", PerfFormat.Count(rt.Gen2)) },
                "Very high allocation rates (large lists/strings per request) or a heap close to its limit.",
                "Reduce allocations in hot paths (streaming instead of buffering, smaller payloads); check the heap trend.",
                Array.Empty<PerfCodeRef>(),
                apiMs * rt.GcPausePct / 100));
        }

        if (d.Period.Span >= TimeSpan.FromHours(6) && rt.GcHeapMbStart is > 50 && rt.GcHeapMbEnd is { } heapEnd
            && heapEnd > rt.GcHeapMbStart.Value * (1 + t.HeapGrowthPct / 100.0))
        {
            f.Add(new PerfFinding(
                "heap-growth", PerfCategory.Code, PerfSeverity.Warning,
                $"Managed memory grew from {rt.GcHeapMbStart:0} MB to {heapEnd:0} MB",
                "A heap that keeps growing over hours can be a memory leak — caches without limits, event handlers never removed, static lists.",
                new[] { new PerfEvidence("Heap at start / end", $"{rt.GcHeapMbStart:0} MB / {heapEnd:0} MB"), new PerfEvidence("Max", $"{rt.GcHeapMbMax ?? 0:0} MB") },
                "Unbounded caches or retained objects.",
                "Watch whether it levels off after a day; if not, take a memory dump (dotnet-gcdump) and look for the biggest retained types.",
                Array.Empty<PerfCodeRef>(),
                heapEnd * 100));
        }
    }

    // ------------------------------------------------------------- network

    private static void NetworkRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        var total = d.RumOf("api_total").ToList();
        var network = d.RumOf("api_network").ToList();
        var calls = total.Sum(x => x.Count);
        if (calls >= 50)
        {
            var totalMs = total.Sum(x => x.SumValue);
            var networkMs = network.Sum(x => x.SumValue);
            var share = totalMs <= 0 ? 0 : 100 * networkMs / totalMs;
            if (share > t.NetworkSharePct)
            {
                f.Add(new PerfFinding(
                    "network-share", PerfCategory.Network, share > 75 ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"{PerfFormat.Pct(share)} of API time in the browser is network, not server",
                    "The browser measures each API call end to end; the server reports its own time in the Server-Timing header. The difference is connection setup, latency and download time — outside the server.",
                    new[]
                    {
                        new PerfEvidence("API calls measured", PerfFormat.Count(calls)),
                        new PerfEvidence("Network share", PerfFormat.Pct(share)),
                        new PerfEvidence("Avg network per call", PerfFormat.Ms(networkMs / Math.Max(1, network.Sum(x => x.Count)))),
                    },
                    "Slow or distant user connections (Wi-Fi, mobile, VPN), proxy overhead, or large responses.",
                    "Check the connection-type breakdown on the Frontend tab; reduce payload sizes and the number of calls per screen. If users are remote, the hosting location matters.",
                    Array.Empty<PerfCodeRef>(),
                    networkMs - totalMs * 0.25));
            }
        }

        var conn = d.RumBreakdown.Where(b => b.Dimension == "connection").ToList();
        var views = conn.Sum(c => c.Count);
        var slow = conn.Where(c => c.Value is "slow-2g" or "2g" or "3g").Sum(c => c.Count);
        if (views >= 50 && slow > views * 0.2)
        {
            f.Add(new PerfFinding(
                "slow-connections", PerfCategory.Network, PerfSeverity.Info,
                $"{PerfFormat.Pct(100.0 * slow / views)} of page views come from slow connections",
                "The browser reports an effective connection type of 3G or worse for these users.",
                new[] { new PerfEvidence("Slow-connection views", PerfFormat.Count(slow)), new PerfEvidence("All views", PerfFormat.Count(views)) },
                "Users on mobile networks or congested Wi-Fi.",
                "Keep payloads small and avoid request waterfalls; nothing to fix on the server.",
                Array.Empty<PerfCodeRef>(),
                slow * 200));
        }
    }

    // ------------------------------------------------------------ frontend

    private static void FrontendRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        void Vital(string metric, string label, double threshold, string unitFormat, string explanation, string action)
        {
            foreach (var r in d.RumOf(metric).Where(r => r.Count >= 10 && r.P75 > threshold))
            {
                var value = unitFormat == "cls" ? (r.P75 / 1000).ToString("0.00", CultureInfo.InvariantCulture) : PerfFormat.Ms(r.P75);
                f.Add(new PerfFinding(
                    metric + ":" + r.Route, PerfCategory.Frontend, r.P75 > threshold * 2 ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"{label} on {r.Route} is {value} (p75)",
                    explanation,
                    new[] { new PerfEvidence("p75", value), new PerfEvidence("Page views", PerfFormat.Count(r.Count)) },
                    "Heavy rendering, large JavaScript or slow data on first paint.",
                    action,
                    new[] { new PerfCodeRef("frontend-route", r.Route) },
                    unitFormat == "cls" ? r.Count * 50 : Math.Max(0, r.P75 - threshold) * r.Count));
            }
        }

        Vital("lcp", "Largest Contentful Paint", t.LcpMs, "ms",
            "LCP is how long until the main content of the page is visible. Above 2.5 s users perceive the page as slow.",
            "Check which API calls the page waits for before rendering; lazy-load heavy components; keep the initial bundle small.");
        Vital("inp", "Interaction to Next Paint", t.InpMs, "ms",
            "INP measures how quickly the page reacts to clicks and typing. Above 200 ms the UI feels sluggish.",
            "Look at the long tasks / scripts listed on the Frontend tab; avoid heavy work in click handlers and huge re-renders.");
        Vital("cls", "Cumulative Layout Shift", t.ClsMilli, "cls",
            "CLS measures how much the layout jumps while loading. Above 0.1 users misclick.",
            "Reserve space for content that loads later (skeletons with fixed heights, image dimensions).");

        var views = d.RumOf("view").ToDictionary(v => v.Route, v => v.Count);
        foreach (var g in d.RumOf("long_task").Where(r => r.MaxValue >= t.LongTaskMs).GroupBy(r => r.Route))
        {
            var count = g.Sum(x => x.Count);
            var v = views.GetValueOrDefault(g.Key, 0);
            if (v < 5 || (double)count / v <= t.LongTasksPerView) continue;
            var scripts = d.RumOf("loaf").Where(x => x.Route == g.Key && x.Detail.Length > 0)
                .OrderByDescending(x => x.SumValue).Take(3).Select(x => x.Detail).ToList();
            f.Add(new PerfFinding(
                "longtask:" + g.Key, PerfCategory.Frontend, PerfSeverity.Warning,
                $"Long JavaScript tasks block {g.Key} ({(double)count / v:0.0} per view)",
                "While a task runs longer than 50 ms the browser cannot respond to input or paint.",
                new[]
                {
                    new PerfEvidence("Long tasks per view", ((double)count / v).ToString("0.0", CultureInfo.InvariantCulture)),
                    new PerfEvidence("Longest", PerfFormat.Ms(g.Max(x => x.MaxValue))),
                    new PerfEvidence("Scripts", scripts.Count == 0 ? "not attributed" : string.Join(", ", scripts)),
                },
                "Large lists rendered at once, heavy computations or big JSON parsing on the main thread.",
                "Virtualise long lists, memoise expensive renders, split work with requestIdleCallback or a worker.",
                new[] { new PerfCodeRef("frontend-route", g.Key) }.Concat(scripts.Select(s => new PerfCodeRef("script", s))).ToList(),
                g.Sum(x => x.SumValue)));
        }

        foreach (var r in d.RumOf("screen_api_calls").Where(r => r.Count >= 5 && r.P75 > t.ApiCallsPerScreen))
        {
            f.Add(new PerfFinding(
                "api-calls:" + r.Route, PerfCategory.Frontend, PerfSeverity.Warning,
                $"Opening {r.Route} fires {r.P75:0} API calls (p75)",
                "Every call costs a round trip and server work; calls that depend on each other form a waterfall.",
                new[] { new PerfEvidence("Calls per screen (p75 / max)", $"{r.P75:0} / {r.MaxValue:0}"), new PerfEvidence("Screens measured", PerfFormat.Count(r.Count)) },
                "Per-item fetching, duplicate queries, or data loaded that the first view does not need.",
                "Combine calls into one endpoint for the screen, deduplicate TanStack Query keys, defer below-the-fold data.",
                new[] { new PerfCodeRef("frontend-route", r.Route) },
                Math.Max(0, r.Avg - t.ApiCallsPerScreen) * r.Count * 50));
        }

        if (d.Bundle is { } bundle && bundle.TotalJsGzipBytes > 1_500_000)
        {
            f.Add(new PerfFinding(
                "bundle", PerfCategory.Frontend, PerfSeverity.Info,
                $"The JavaScript bundle is large ({PerfFormat.Bytes(bundle.TotalJsGzipBytes)} gzipped)",
                "Big bundles delay the first load (download + parse), mostly on slower machines.",
                new[] { new PerfEvidence("JS (raw / gzip)", $"{PerfFormat.Bytes(bundle.TotalJsBytes)} / {PerfFormat.Bytes(bundle.TotalJsGzipBytes)}") },
                "Heavy libraries in the entry chunk.",
                "Lazy-load rarely used pages and heavy libraries (charts, editors, 3D).",
                bundle.Chunks.OrderByDescending(c => c.GzipBytes).Take(3).Select(c => new PerfCodeRef("chunk", c.Name)).ToList(),
                bundle.TotalJsGzipBytes / 1000.0));
        }
    }

    // ---------------------------------------------------------- background

    private static void BackgroundRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        foreach (var o in d.WorkerOverlaps.Where(o => o.RequestsDuring >= 50 && o.P95Outside > 0
                                                      && o.P95During > o.P95Outside * (1 + t.WorkerOverlapPct / 100.0)))
        {
            f.Add(new PerfFinding(
                "worker-overlap:" + o.Worker, PerfCategory.Background, PerfSeverity.Warning,
                $"The API is slower while '{o.Worker}' runs",
                $"API p95 is {PerfFormat.Ms(o.P95During)} during runs of this worker vs {PerfFormat.Ms(o.P95Outside)} otherwise.",
                new[]
                {
                    new PerfEvidence("p95 during / outside", $"{PerfFormat.Ms(o.P95During)} / {PerfFormat.Ms(o.P95Outside)}"),
                    new PerfEvidence("Minutes overlapping", PerfFormat.Count(o.MinutesDuring)),
                },
                "The worker competes for CPU, database connections or locks with user requests.",
                "Make the job lighter (smaller batches, pauses between batches), schedule it outside office hours, or lower its frequency.",
                new[] { new PerfCodeRef("worker", o.Worker) },
                (o.P95During - o.P95Outside) * o.RequestsDuring * 0.2));
        }

        foreach (var w in d.Workers.Where(w => w.Runs >= 5 && w.Failures > w.Runs * 0.2))
        {
            f.Add(new PerfFinding(
                "worker-failing:" + w.Worker, PerfCategory.Background, PerfSeverity.Warning,
                $"Worker '{w.Worker}' fails often ({w.Failures} of {w.Runs} runs)",
                "Failed runs are retried and waste resources; the data it syncs may be stale.",
                new[] { new PerfEvidence("Failures", $"{w.Failures} / {w.Runs}") },
                "An integration error (credentials, throttling, remote outage).",
                "See the Health page integrations card and the incident log for the error.",
                new[] { new PerfCodeRef("worker", w.Worker) },
                w.TotalMs * 0.1));
        }

        foreach (var x in d.SpansOf("external").GroupBy(s => s.Name))
        {
            var count = x.Sum(s => s.Count);
            var errors = x.Sum(s => s.Errors);
            var throttled = x.Where(s => s.Detail.EndsWith("429", StringComparison.Ordinal)).Sum(s => s.Count);
            if (count < 20 || errors <= count * t.ExternalErrorPct / 100.0) continue;
            f.Add(new PerfFinding(
                "external-errors:" + x.Key, PerfCategory.Background, throttled > 0 ? PerfSeverity.Warning : PerfSeverity.Info,
                throttled > 0 ? $"{x.Key} throttles us ({PerfFormat.Count(throttled)} × 429)" : $"Calls to {x.Key} fail often ({PerfFormat.Pct(100.0 * errors / count)})",
                "Failing or throttled external calls are retried, slow down workers and — when called from a request — the user.",
                new[] { new PerfEvidence("Calls", PerfFormat.Count(count)), new PerfEvidence("Errors", PerfFormat.Count(errors)), new PerfEvidence("Throttled (429)", PerfFormat.Count(throttled)) },
                throttled > 0 ? "Too many calls in a short time for the API's quota." : "Remote errors or timeouts.",
                throttled > 0 ? "Spread the calls (longer poll intervals, smaller batches) and honour Retry-After." : "Check the integration health and credentials.",
                new[] { new PerfCodeRef("host", x.Key) },
                x.Sum(s => s.SumMs) * 0.3));
        }

        foreach (var h in d.SpansOf("hub").Where(h => h.Count >= 20 && h.P95 > 500))
        {
            f.Add(new PerfFinding(
                "hub:" + h.Name + "." + h.Detail, PerfCategory.Background, PerfSeverity.Warning,
                $"SignalR method {h.Name}.{h.Detail} is slow (p95 {PerfFormat.Ms(h.P95)})",
                "Realtime calls from the browser should be near-instant; slow ones delay presence and live updates.",
                new[] { new PerfEvidence("Calls", PerfFormat.Count(h.Count)), new PerfEvidence("p95", PerfFormat.Ms(h.P95)) },
                "Database work inside the hub method.",
                "Keep hub methods thin: cache or defer the work.",
                new[] { new PerfCodeRef("hub", h.Name + "." + h.Detail) },
                h.SumMs));
        }

        var minutes = Math.Max(1, d.Period.Span.TotalMinutes);
        foreach (var b in d.SpansOf("broadcast").Where(b => b.Detail.EndsWith("all", StringComparison.Ordinal) && b.Count / minutes > 60))
        {
            f.Add(new PerfFinding(
                "broadcast:" + b.Name + ":" + b.Detail, PerfCategory.Background, PerfSeverity.Info,
                $"{b.Name} broadcasts '{b.Detail}' {b.Count / minutes:0} times per minute",
                "Messages to all clients are multiplied by the number of open tabs; each one may trigger refetches in every browser.",
                new[] { new PerfEvidence("Messages", PerfFormat.Count(b.Count)) },
                "A broadcast per change instead of a targeted or debounced update.",
                "Send to groups (per ticket / per queue) or coalesce bursts.",
                new[] { new PerfCodeRef("hub", b.Name + " " + b.Detail) },
                b.Count * 5));
        }

        foreach (var s in d.SpansOf("search").Where(s => s.Count >= 20 && s.P95 > 1000))
        {
            f.Add(new PerfFinding(
                "search:" + s.Name, PerfCategory.Code, PerfSeverity.Warning,
                $"Global search source '{s.Name}' is slow (p95 {PerfFormat.Ms(s.P95)})",
                "Global search waits for every source; the slowest one decides how fast results appear.",
                new[] { new PerfEvidence("Searches", PerfFormat.Count(s.Count)), new PerfEvidence("p95", PerfFormat.Ms(s.P95)) },
                "Missing full-text/trigram index or a broad ILIKE.",
                "Check the source's query plan; add a GIN index or narrow the match.",
                new[] { new PerfCodeRef("search-source", s.Name) },
                s.SumMs * 0.5));
        }
    }

    private static void RegressionRules(PerfDataset d, FindingThresholds t, List<PerfFinding> f)
    {
        if (d.VersionComparison is not { } cmp) return;
        foreach (var r in cmp.Routes.Where(r => r.BeforeCount >= t.RouteMinRequests && r.AfterCount >= t.RouteMinRequests
                                               && r.ChangePct > t.RegressionPct && r.AfterP95 > 200))
        {
            f.Add(new PerfFinding(
                "regression:" + r.Method + " " + r.Route, PerfCategory.Code, r.ChangePct > t.RegressionPct * 3 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"{r.Method} {r.Route} got {r.ChangePct:0}% slower in {cmp.After}",
                $"p95 went from {PerfFormat.Ms(r.BeforeP95)} in {cmp.Before} to {PerfFormat.Ms(r.AfterP95)} in {cmp.After}.",
                new[]
                {
                    new PerfEvidence(cmp.Before, $"{PerfFormat.Ms(r.BeforeP95)} ({PerfFormat.Count(r.BeforeCount)} req.)"),
                    new PerfEvidence(cmp.After, $"{PerfFormat.Ms(r.AfterP95)} ({PerfFormat.Count(r.AfterCount)} req.)"),
                },
                "A change in this release (new query, extra data loaded, new middleware).",
                $"Diff the code behind this route between {cmp.Before} and {cmp.After}.",
                new[] { new PerfCodeRef("route", r.Method + " " + r.Route) },
                (r.AfterP95 - r.BeforeP95) * r.AfterCount * 0.1));
        }
    }

    private static void MonitorRules(PerfDataset d, List<PerfFinding> f)
    {
        var access = d.PgAccess;
        if (access.Checked && (!access.StatStatements || !access.PgMonitor))
        {
            f.Add(new PerfFinding(
                "pg-access", PerfCategory.Maintenance, PerfSeverity.Info,
                "Database statistics are limited",
                access.StatStatementsReason ?? "The app role cannot read all PostgreSQL statistics.",
                new[] { new PerfEvidence("pg_monitor", access.PgMonitor ? "yes" : "no"), new PerfEvidence("pg_stat_statements", access.StatStatements ? "readable" : "not available") },
                "deploy/update.sh has not run since this version, or the extension is missing.",
                "Run deploy/update.sh on the host; it grants pg_monitor and enables pg_stat_statements.",
                Array.Empty<PerfCodeRef>(),
                0));
        }

        if (d.Runtime.OverheadMsPerRequest > 1)
        {
            f.Add(new PerfFinding(
                "overhead", PerfCategory.Code, PerfSeverity.Info,
                $"Monitoring costs {d.Runtime.OverheadMsPerRequest:0.00} ms per request",
                "The collectors measure their own cost. More than a millisecond per request is more than expected.",
                new[] { new PerfEvidence("Overhead per request", PerfFormat.Ms(d.Runtime.OverheadMsPerRequest)) },
                "Diagnose mode is on, or a collector is unusually busy.",
                "Switch Diagnose off, or disable the most expensive collector in Settings.",
                Array.Empty<PerfCodeRef>(),
                d.Runtime.OverheadMs));
        }
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}

public static class PerfFormat
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Ms(double ms) => ms >= 10_000 ? (ms / 1000).ToString("0.0 's'", C)
        : ms >= 1000 ? (ms / 1000).ToString("0.00 's'", C)
        : ms >= 10 ? ms.ToString("0 'ms'", C)
        : ms.ToString("0.0 'ms'", C);

    public static string Duration(double ms)
    {
        if (ms < 1000) return Ms(ms);
        var s = ms / 1000;
        if (s < 120) return s.ToString("0.0 's'", C);
        var m = s / 60;
        if (m < 120) return m.ToString("0.0 'min'", C);
        return (m / 60).ToString("0.0 'h'", C);
    }

    public static string Count(long n) => n.ToString("#,0", C);
    public static string Pct(double p) => p.ToString(p < 10 ? "0.0'%'" : "0'%'", C);
    public static string Kb(double kb) => kb >= 1024 ? (kb / 1024).ToString("0.0 'MB'", C) : kb.ToString("0 'KB'", C);

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.0 'GB'", C),
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.0 'MB'", C),
        >= 1L << 10 => (b / 1024.0).ToString("0 'KB'", C),
        _ => b.ToString("0 'B'", C),
    };

    public static string Date(DateTime? utc) => utc is null ? "never" : utc.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", C);
}
