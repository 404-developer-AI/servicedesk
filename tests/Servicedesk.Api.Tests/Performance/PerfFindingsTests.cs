using System.Text.Json;
using Servicedesk.Infrastructure.Performance;
using Xunit;

namespace Servicedesk.Api.Tests.Performance;

/// The rule engine and the export are pure functions of a dataset, so they
/// are tested with fixed fixtures.
public sealed class PerfFindingsTests
{
    internal static long[] Hist(params (double Value, long Count)[] points)
    {
        var h = new long[PerfHistogram.BucketCount];
        foreach (var (v, c) in points) h[PerfHistogram.IndexOf(v)] += c;
        return h;
    }

    internal static HttpRouteStats Route(string route, long count, double typicalMs, double dbShare, double extShare = 0, double pipelineShare = 0,
        long dbQueriesPerRequest = 3, double kb = 5)
    {
        var sum = count * typicalMs;
        return new HttpRouteStats
        {
            Method = "GET",
            Route = route,
            Count = count,
            SumMs = sum,
            MaxMs = typicalMs * 1.5,
            Hist = Hist((typicalMs, count)),
            DbMs = sum * dbShare,
            ExtMs = sum * extShare,
            PipelineMs = sum * pipelineShare,
            DbCount = count * dbQueriesPerRequest,
            Bytes = (long)(count * kb * 1024),
            SizeHist = Hist((kb, count)),
        };
    }

    internal static PerfDataset Dataset(Action<DatasetBuilder>? configure = null)
    {
        var b = new DatasetBuilder();
        configure?.Invoke(b);
        return b.Build();
    }

    internal sealed class DatasetBuilder
    {
        public List<HttpRouteStats> Routes { get; } = new();
        public List<DbQueryStats> Queries { get; } = new();
        public List<NPlusOneStats> NPlusOne { get; } = new();
        public List<PgStatementDelta> Pg { get; } = new();
        public List<TableDelta> TableDeltas { get; } = new();
        public List<PgInspector.TableRow> Tables { get; } = new();
        public List<RumStats> Rum { get; } = new();
        public List<SpanStats> Spans { get; } = new();
        public List<WorkerOverlap> Overlaps { get; } = new();
        public List<Budget> Budgets { get; } = new();
        public List<MarkerRow> Markers { get; } = new();
        public RuntimeSummary Runtime { get; set; } = new();
        public DbPeriodStats PgDb { get; set; } = new();
        public VersionComparisonResult? Versions { get; set; }
        public FindingThresholds Thresholds { get; } = new();

        public PerfDataset Build()
        {
            var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            HttpRouteStats? total = null;
            if (Routes.Count > 0)
            {
                total = new HttpRouteStats
                {
                    Method = "*",
                    Route = "*",
                    Count = Routes.Sum(r => r.Count),
                    SumMs = Routes.Sum(r => r.SumMs),
                    DbMs = Routes.Sum(r => r.DbMs),
                    Hist = PerfHistogram.Merge(Routes.Select(r => (IReadOnlyList<long>)r.Hist)),
                    MaxMs = Routes.Max(r => r.MaxMs),
                };
            }
            return new PerfDataset
            {
                Period = new PerfPeriod(now.AddHours(-24), now, true, now.UtcDateTime, now.UtcDateTime, now.UtcDateTime, now.UtcDateTime, 900),
                GeneratedUtc = now,
                Options = PerfOptions.Defaults,
                Level = PerfLevel.Basic,
                Thresholds = Thresholds,
                Total = total,
                Routes = Routes,
                Queries = Queries,
                NPlusOne = NPlusOne,
                PgStatements = Pg,
                PgDatabase = PgDb,
                TableDeltas = TableDeltas,
                Tables = Tables,
                Runtime = Runtime,
                Spans = Spans,
                Rum = Rum,
                WorkerOverlaps = Overlaps,
                Budgets = Budgets,
                Markers = Markers,
                VersionComparison = Versions,
                PgAccess = new PgAccess(true, 160000, "16.4", true, true, null),
                HostSupported = true,
            };
        }
    }

    [Fact]
    public void SlowDatabaseBoundRoute_IsADatabaseFinding()
    {
        var d = Dataset(b => b.Routes.Add(Route("/api/tickets/{id:guid}", 500, 1800, dbShare: 0.8)));
        var f = Assert.Single(PerfFindings.Evaluate(d), x => x.Key.StartsWith("route-", StringComparison.Ordinal));
        Assert.Equal(PerfCategory.Database, f.Category);
        Assert.Contains(f.CodeRefs, r => r.Kind == "route" && r.Value == "GET /api/tickets/{id:guid}");
    }

    [Fact]
    public void SlowCodeBoundRoute_IsACodeFinding_AndExternalBoundIsSeparate()
    {
        var d = Dataset(b =>
        {
            b.Routes.Add(Route("/api/reports", 100, 2500, dbShare: 0.1));
            b.Routes.Add(Route("/api/graph", 100, 2500, dbShare: 0.05, extShare: 0.8));
        });
        var findings = PerfFindings.Evaluate(d);
        Assert.Contains(findings, x => x.Key == "route-app:GET /api/reports" && x.Category == PerfCategory.Code);
        Assert.Contains(findings, x => x.Key == "route-ext:GET /api/graph");
    }

    [Fact]
    public void FastOrRareRoutes_AreIgnored()
    {
        var d = Dataset(b =>
        {
            b.Routes.Add(Route("/api/fast", 10_000, 40, dbShare: 0.9));
            b.Routes.Add(Route("/api/rare", 3, 9000, dbShare: 0.9));
        });
        Assert.DoesNotContain(PerfFindings.Evaluate(d), x => x.Key.StartsWith("route-", StringComparison.Ordinal));
    }

    [Fact]
    public void NPlusOne_IsReported_WithCaller()
    {
        var d = Dataset(b => b.NPlusOne.Add(new NPlusOneStats
        {
            Route = "GET /api/tickets",
            Fingerprint = "abcdef123456",
            Sql = "SELECT name FROM companies WHERE id = @Id",
            Caller = "CompanyRepository.GetAsync",
            Requests = 200,
            Executions = 8000,
            MaxPerRequest = 60,
        }));
        var f = Assert.Single(PerfFindings.Evaluate(d), x => x.Key.StartsWith("n1:", StringComparison.Ordinal));
        Assert.Equal(PerfSeverity.Critical, f.Severity);
        Assert.Contains(f.CodeRefs, r => r.Caller == "CompanyRepository.GetAsync");
    }

    [Fact]
    public void ImpactRanking_PrefersFrequentModerateCostOverRareSlow()
    {
        var d = Dataset(b =>
        {
            b.Queries.Add(new DbQueryStats { Fingerprint = "aaaaaaaaaaaa", Sql = "SELECT 1", Count = 100_000, SumMs = 100_000 * 250, MaxMs = 400, Hist = Hist((250, 100_000)) });
            b.Queries.Add(new DbQueryStats { Fingerprint = "bbbbbbbbbbbb", Sql = "SELECT 2", Count = 6, SumMs = 6 * 3000, MaxMs = 3000, Hist = Hist((3000, 6)) });
        });
        var slow = PerfFindings.Evaluate(d).Where(x => x.Key.StartsWith("query-slow:", StringComparison.Ordinal)).ToList();
        Assert.Equal("query-slow:aaaaaaaaaaaa", slow[0].Key);
    }

    [Fact]
    public void HostingProblems_AreAttributedToHosting()
    {
        var d = Dataset(b =>
        {
            b.Routes.Add(Route("/api/x", 1000, 100, 0.2));
            b.Runtime = new RuntimeSummary
            {
                StealPct = 12, StealMax = 30, HostMinutes = 100, Minutes = 100,
                MemTotalMb = 4000, MemAvailableMinMb = 100, SwapActivity = 5000,
                RootFreePct = 3, RootFreeGb = 1,
            };
        });
        var findings = PerfFindings.Evaluate(d);
        Assert.Contains(findings, x => x.Key == "cpu-steal" && x.Category == PerfCategory.Hosting && x.Severity == PerfSeverity.Critical);
        Assert.Contains(findings, x => x.Key == "memory" && x.Severity == PerfSeverity.Critical);
        Assert.Contains(findings, x => x.Key.StartsWith("disk-free:", StringComparison.Ordinal) && x.Severity == PerfSeverity.Critical);
    }

    [Fact]
    public void MaintenanceRules_DetectDeadRowsSeqScansAndCacheMisses()
    {
        var d = Dataset(b =>
        {
            b.Tables.Add(new PgInspector.TableRow { Name = "ticket_events", Rows = 100_000, Dead = 60_000, ModSinceAnalyze = 50_000 });
            b.TableDeltas.Add(new TableDelta { Name = "tickets", Rows = 200_000, SeqScan = 500, IdxScan = 10, SeqTupRead = 100_000_000 });
            b.PgDb = new DbPeriodStats
            {
                Start = new Dictionary<string, double?> { ["blks_hit"] = 0, ["blks_read"] = 0, ["stats_reset_epoch"] = 1 },
                End = new Dictionary<string, double?> { ["blks_hit"] = 850, ["blks_read"] = 150, ["stats_reset_epoch"] = 1 },
            };
        });
        var findings = PerfFindings.Evaluate(d);
        Assert.Contains(findings, x => x.Key == "dead:ticket_events" && x.Category == PerfCategory.Maintenance);
        Assert.Contains(findings, x => x.Key == "analyze:ticket_events");
        Assert.Contains(findings, x => x.Key == "seqscan:tickets" && x.Category == PerfCategory.Database);
        Assert.Contains(findings, x => x.Key == "cache-hit" && x.Severity == PerfSeverity.Critical);
    }

    [Fact]
    public void ThreadPoolStarvation_NeedsAQueueAndGrowingThreads()
    {
        var grow = Dataset(b => b.Runtime = new RuntimeSummary { Samples = 100, TpQueueSamples = 50, TpThreadsStart = 10, TpThreadsEnd = 40, TpThreadsMax = 40 });
        var stable = Dataset(b => b.Runtime = new RuntimeSummary { Samples = 100, TpQueueSamples = 50, TpThreadsStart = 10, TpThreadsEnd = 10, TpThreadsMax = 10 });
        Assert.Contains(PerfFindings.Evaluate(grow), x => x.Key == "threadpool");
        Assert.DoesNotContain(PerfFindings.Evaluate(stable), x => x.Key == "threadpool");
    }

    [Fact]
    public void NetworkAndFrontendRules_UseRealUserData()
    {
        var d = Dataset(b =>
        {
            b.Rum.Add(new RumStats { Route = "/tickets/$ticketId", Metric = "api_total", Detail = "/api/tickets/{id}", Count = 200, SumValue = 200 * 400 });
            b.Rum.Add(new RumStats { Route = "/tickets/$ticketId", Metric = "api_network", Detail = "/api/tickets/{id}", Count = 200, SumValue = 200 * 300 });
            b.Rum.Add(new RumStats { Route = "/tickets/$ticketId", Metric = "lcp", Count = 50, SumValue = 50 * 4200, MaxValue = 6000, Hist = Hist((4200, 50)) });
        });
        var findings = PerfFindings.Evaluate(d);
        Assert.Contains(findings, x => x.Key == "network-share" && x.Category == PerfCategory.Network);
        Assert.Contains(findings, x => x.Key == "lcp:/tickets/$ticketId" && x.Category == PerfCategory.Frontend);
    }

    [Fact]
    public void WorkerOverlap_AndRegression_AndBudget()
    {
        var d = Dataset(b =>
        {
            b.Routes.Add(Route("/api/tickets/{id:guid}", 300, 450, 0.3));
            b.Overlaps.Add(new WorkerOverlap("adsolut-sync", 30, 600, P95During: 900, P95Outside: 300, MinutesOutside: 30));
            b.Versions = new VersionComparisonResult("0.1.23", "0.1.24", new[]
            {
                new RouteComparison { Method = "GET", Route = "/api/tickets", BeforeCount = 500, BeforeP95 = 200, AfterCount = 500, AfterP95 = 600 },
            });
            b.Budgets.Add(new Budget { Method = "GET", Route = "/api/tickets/{id:guid}", TargetMs = 300, Count = 300, P95 = 450 });
        });
        var findings = PerfFindings.Evaluate(d);
        Assert.Contains(findings, x => x.Key == "worker-overlap:adsolut-sync" && x.Category == PerfCategory.Background);
        Assert.Contains(findings, x => x.Key == "regression:GET /api/tickets");
        Assert.Contains(findings, x => x.Key == "budget:GET /api/tickets/{id:guid}");
    }

    [Fact]
    public void Scores_MarkCategoriesWithoutData()
    {
        var d = Dataset();
        var scores = PerfFindings.Score(d, PerfFindings.Evaluate(d));
        Assert.Equal(7, scores.Count);
        Assert.All(scores.Where(s => s.Key is PerfCategory.Frontend or PerfCategory.Network), s => Assert.Equal("nodata", s.Status));
    }
}

public sealed class PerfReportTests
{
    /// Build a dataset where personal data was smuggled into every free-text
    /// channel, and prove none of it reaches the export.
    [Fact]
    public void Export_ContainsNoPersonalData()
    {
        const string email = "jan.peeters@example.be";
        const string guid = "3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f";
        const string ip = "203.0.113.77";
        const string literal = "Printer broken at reception";

        var d = PerfFindingsTests.Dataset(b =>
        {
            b.Routes.Add(PerfFindingsTests.Route("/api/tickets/{id:guid}", 500, 1800, dbShare: 0.8));
            b.Queries.Add(new DbQueryStats
            {
                Fingerprint = "abcdef123456",
                Sql = $"SELECT * FROM tickets WHERE id = 123 AND subject = '{literal}' AND requester = '{email}'",
                Caller = "TicketRepository.GetAsync",
                Count = 50,
                SumMs = 50 * 900,
                MaxMs = 1200,
                Hist = PerfFindingsTests.Hist((900, 50)),
                Sources = { new SourceShare { Fingerprint = "abcdef123456", Source = "GET /api/tickets/{id:guid}", Count = 50, SumMs = 45_000 } },
            });
            b.Pg.Add(new PgStatementDelta { QueryId = 42, Query = $"UPDATE contacts SET email = '{email}' WHERE id = '{guid}'", Calls = 10, TotalMs = 9000 });
            b.Markers.Add(new MarkerRow { Id = 1, Kind = "manual", Label = $"Moved {email} from {ip} ticket 98765", TsUtc = DateTime.UtcNow });
        });
        var findings = PerfFindings.Evaluate(d);
        var scores = PerfFindings.Score(d, findings);
        var builder = new PerfReportBuilder();
        var markdown = builder.BuildMarkdown(d, findings, scores, "Europe/Brussels");
        var json = builder.BuildJson(d, findings, scores);

        foreach (var text in new[] { markdown, json })
        {
            Assert.DoesNotContain(email, text);
            Assert.DoesNotContain(guid, text);
            Assert.DoesNotContain(ip, text);
            Assert.DoesNotContain(literal, text);
            Assert.DoesNotContain("98765", text);
            Assert.DoesNotContain("id = 123", text);
        }
        Assert.Contains("TicketRepository.GetAsync", markdown);
        Assert.Contains("/api/tickets/{id:guid}", markdown);
    }

    [Fact]
    public void Export_IsValidJson_WithSchemaVersion_AndCompactMarkdown()
    {
        var d = PerfFindingsTests.Dataset(b =>
        {
            for (var i = 0; i < 400; i++) b.Routes.Add(PerfFindingsTests.Route($"/api/route{i}/{{id:guid}}", 100, 1500 + i, dbShare: 0.7));
        });
        var findings = PerfFindings.Evaluate(d);
        var scores = PerfFindings.Score(d, findings);
        var builder = new PerfReportBuilder();
        var markdown = builder.BuildMarkdown(d, findings, scores, "UTC");
        using var doc = JsonDocument.Parse(builder.BuildJson(d, findings, scores));

        Assert.Equal(PerfReportBuilder.SchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(global::System.Text.Encoding.UTF8.GetByteCount(markdown) <= 100 * 1024);
        Assert.StartsWith("# Servicedesk performance report", markdown);
        Assert.Contains("## Instructions for Claude Code", markdown);
    }

    [Fact]
    public void Zip_HoldsReportDataAndPlans()
    {
        var zip = new PerfReportBuilder().BuildZip("# r", "{}", new Dictionary<string, string> { ["pgss:42"] = "[]" });
        using var archive = new global::System.IO.Compression.ZipArchive(new MemoryStream(zip));
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("performance-report.md", names);
        Assert.Contains("performance-data.json", names);
        Assert.Contains("query-plans/pgss-42.json", names);
    }

    [Fact]
    public void Numbers_RenderInvariant_RegardlessOfCulture()
    {
        var previous = global::System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            global::System.Globalization.CultureInfo.CurrentCulture = new global::System.Globalization.CultureInfo("nl-BE");
            var d = PerfFindingsTests.Dataset(b => b.Routes.Add(PerfFindingsTests.Route("/api/x", 100, 1500, dbShare: 0.9, dbQueriesPerRequest: 3)));
            var findings = PerfFindings.Evaluate(d);
            var f = findings.First(x => x.Key.StartsWith("route-db:", StringComparison.Ordinal));
            Assert.Contains("3.0 queries", f.Explanation);
        }
        finally
        {
            global::System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void WorkerOverlap_ComparesMinutesDuringAndOutsideRuns()
    {
        var start = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
        var minutes = Enumerable.Range(0, 60).Select(i => new TimePoint
        {
            T = start.AddMinutes(i),
            Count = 10,
            Hist = PerfFindingsTests.Hist((i is >= 10 and < 20 ? 900 : 100, 10)),
            MaxMs = i is >= 10 and < 20 ? 900 : 100,
        }).ToList();
        var runs = new[] { new WorkerRunRow { Worker = "sync", StartedUtc = start.AddMinutes(10), DurationMs = 9.5 * 60_000, Success = true } };
        var o = Assert.Single(PerfDatasetBuilder.ComputeOverlaps(minutes, runs));
        Assert.True(o.P95During > o.P95Outside * 3);
        Assert.Equal(50, o.MinutesOutside);
    }

    [Theory]
    [InlineData(4, 900, 300)]   // near-always-on worker: too few minutes without it
    [InlineData(30, 140, 60)]   // ratio exceeded but the absolute gap is tiny
    public void WorkerOverlap_NeedsABaselineAndARealGap(long minutesOutside, double during, double outside)
    {
        var d = PerfFindingsTests.Dataset(b => b.Overlaps.Add(
            new WorkerOverlap("telavox-polling", 56, 600, P95During: during, P95Outside: outside, MinutesOutside: minutesOutside)));
        Assert.DoesNotContain(PerfFindings.Evaluate(d), x => x.Key == "worker-overlap:telavox-polling");
    }
}
