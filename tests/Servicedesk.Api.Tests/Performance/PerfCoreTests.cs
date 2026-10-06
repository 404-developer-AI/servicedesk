using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests.Performance;

public sealed class PerfHistogramTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1.5, 1)]
    [InlineData(100, 11)]
    [InlineData(100.1, 12)]
    [InlineData(60_000, 26)]
    [InlineData(60_001, 27)]
    public void IndexOf_UsesInclusiveUpperBounds(double value, int expected)
    {
        Assert.Equal(expected, PerfHistogram.IndexOf(value));
    }

    [Fact]
    public void Percentiles_OfUniformData_AreWithinOneBucket()
    {
        var counts = new long[PerfHistogram.BucketCount];
        for (var v = 1; v <= 1000; v++) counts[PerfHistogram.IndexOf(v)]++;

        var p50 = PerfHistogram.Percentile(counts, 50, 1000);
        var p95 = PerfHistogram.Percentile(counts, 95, 1000);

        Assert.InRange(p50, 300, 750);   // true 500; bucket (300,500]/(500,750]
        Assert.InRange(p95, 750, 1000);  // true 950
        Assert.True(p95 > p50);
    }

    [Fact]
    public void Percentile_NeverExceedsObservedMax()
    {
        var counts = new long[PerfHistogram.BucketCount];
        counts[PerfHistogram.IndexOf(140)] = 10; // bucket (100,150]
        Assert.True(PerfHistogram.Percentile(counts, 99, observedMax: 120) <= 120);
    }

    [Fact]
    public void Merge_IsAdditive_SoRollupsKeepPercentiles()
    {
        var minuteA = new long[PerfHistogram.BucketCount];
        var minuteB = new long[PerfHistogram.BucketCount];
        var combined = new long[PerfHistogram.BucketCount];
        var rnd = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var v = rnd.NextDouble() * 2000;
            (i % 2 == 0 ? minuteA : minuteB)[PerfHistogram.IndexOf(v)]++;
            combined[PerfHistogram.IndexOf(v)]++;
        }
        var hour = PerfHistogram.Merge(new IReadOnlyList<long>[] { minuteA, minuteB });
        Assert.Equal(combined, hour);
        Assert.Equal(PerfHistogram.Percentile(combined, 95), PerfHistogram.Percentile(hour, 95));
    }

    [Fact]
    public void Percentile_OfEmptyHistogram_IsZero()
    {
        Assert.Equal(0, PerfHistogram.Percentile(new long[PerfHistogram.BucketCount], 95));
    }
}

public sealed class PerfAggregateTests
{
    [Fact]
    public void Record_IsThreadSafe()
    {
        var agg = new PerfAggregate();
        Parallel.For(0, 10_000, i => agg.Record(i % 100));
        Assert.Equal(10_000, agg.Count);
        Assert.Equal(10_000, agg.Buckets.Sum());
        Assert.Equal(99, agg.MaxValue);
    }

    [Fact]
    public void Window_CapsKeys_IntoOtherBucket()
    {
        var window = new PerfWindow(DateTimeOffset.UtcNow);
        for (var i = 0; i < PerfWindow.MaxHttpKeys + 50; i++)
        {
            window.HttpFor(new HttpKey("GET", "/api/r" + i, "2xx")).Record(1);
        }
        Assert.True(window.Http.Count <= PerfWindow.MaxHttpKeys + 1);
        Assert.Contains(window.Http.Keys, k => k.Route == PerfWindow.OtherKey);
    }
}

public sealed class SqlFingerprintTests
{
    [Fact]
    public void Literals_AreReplaced_AndShapesMatch()
    {
        var a = SqlFingerprint.Get("SELECT * FROM tickets WHERE id = 123 AND subject = 'Printer broken'");
        var b = SqlFingerprint.Get("SELECT *   FROM tickets\nWHERE id = 9 AND subject = 'x'");
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.DoesNotContain("123", a.Normalized);
        Assert.DoesNotContain("Printer", a.Normalized);
    }

    [Fact]
    public void ExpandedParameterLists_Collapse()
    {
        var a = SqlFingerprint.Normalize("SELECT 1 FROM t WHERE id IN (@ids1, @ids2, @ids3)");
        var b = SqlFingerprint.Normalize("SELECT 1 FROM t WHERE id IN (@ids1,@ids2)");
        Assert.Equal(a, b);
        Assert.Contains("(...)", a);
    }

    [Fact]
    public void Comments_DollarQuotes_AndEscapeStrings_AreRemoved()
    {
        var n = SqlFingerprint.Normalize("/* secret */ SELECT $$ hidden body $$, E'a\\'b' -- trailing note\nFROM x");
        Assert.DoesNotContain("secret", n);
        Assert.DoesNotContain("hidden", n);
        Assert.DoesNotContain("trailing", n);
        Assert.DoesNotContain("a\\'b", n);
    }

    [Fact]
    public void ParameterNames_AreKept()
    {
        var n = SqlFingerprint.Normalize("SELECT * FROM tickets WHERE id = @TicketId AND x = $1");
        Assert.Contains("@TicketId", n);
        Assert.Contains("$1", n);
    }

    [Fact]
    public void ToPositional_MapsNamedParameters()
    {
        var sql = SqlFingerprint.ToPositional("SELECT * FROM t WHERE a = @A AND b = @B AND c = @A");
        Assert.Equal("SELECT * FROM t WHERE a = $1 AND b = $2 AND c = $1", sql);
    }

    [Fact]
    public void ValuesLists_Collapse()
    {
        var n = SqlFingerprint.Normalize("INSERT INTO t (a, b) VALUES (1, 'x'), (2, 'y'), (3, 'z')");
        Assert.Equal("INSERT INTO t (a, b) VALUES (...)", n);
    }
}

public sealed class PerfRedactorTests
{
    [Theory]
    [InlineData("contact jan.peeters@example.be please", "jan.peeters@example.be")]
    [InlineData("ticket 3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f opened", "3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f")]
    [InlineData("from 192.168.10.42", "192.168.10.42")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")]
    [InlineData("key abcdef0123456789abcdef0123456789abcd", "abcdef0123456789abcdef0123456789abcd")]
    public void Text_MasksIdentifyingValues(string input, string secret)
    {
        Assert.DoesNotContain(secret, PerfRedactor.Text(input));
    }

    [Fact]
    public void Text_KeepsLongSnakeCaseIdentifiers()
    {
        const string index = "ix_ticket_events_author_created_with_a_very_long_name";
        Assert.Contains(index, PerfRedactor.Text("drop " + index));
    }

    [Fact]
    public void Sql_RemovesValuesFromWhereClauses()
    {
        var s = PerfRedactor.Sql("SELECT * FROM contacts WHERE id = 123 AND email = 'jan@example.be' AND name = 'Jan Peeters'");
        Assert.DoesNotContain("123", s);
        Assert.DoesNotContain("jan@example.be", s);
        Assert.DoesNotContain("Peeters", s);
        Assert.Contains("contacts", s);
    }

    [Fact]
    public void MaskNumbers_OnlyWhenAsked()
    {
        Assert.Contains("12345", PerfRedactor.Text("ticket 12345"));
        Assert.DoesNotContain("12345", PerfRedactor.Text("ticket 12345", maskNumbers: true));
    }

    [Theory]
    [InlineData("/tickets/3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f", "/tickets/{id}")]
    [InlineData("/tickets/$ticketId", "/tickets/$ticketId")]
    [InlineData("/api/tickets/123/events?x=secret", "/api/tickets/{id}/events")]
    [InlineData("/contacts/jan@example.be", "/contacts/{id}")]
    public void Route_ScrubsIdSegmentsAndQueryStrings(string input, string expected)
    {
        Assert.Equal(expected, PerfRedactor.Route(input));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("/a b")]
    [InlineData("")]
    public void Route_RejectsGarbage(string input)
    {
        Assert.Null(PerfRedactor.Route(input));
    }
}

public sealed class PerfSettingsTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Diagnose_ExpiresOnServerTime_BackToBasic()
    {
        var store = new InMemorySettingsService();
        var time = new ManualTime();
        store.Set(SettingKeys.Performance.DiagnoseUntilUtc, PerfSettingsProvider.FormatUtc(time.Now.AddMinutes(60)));
        var perf = new PerfSettingsProvider(store, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<PerfSettingsProvider>.Instance);
        await perf.RefreshAsync(default);

        Assert.Equal(PerfLevel.Diagnose, perf.Level);
        time.Now = time.Now.AddMinutes(59);
        Assert.Equal(PerfLevel.Diagnose, perf.Level);
        time.Now = time.Now.AddMinutes(2);
        Assert.Equal(PerfLevel.Basic, perf.Level);
    }

    [Fact]
    public async Task Off_DisablesEveryCollector_AndDiagnose()
    {
        var store = new InMemorySettingsService();
        var time = new ManualTime();
        store.Set(SettingKeys.Performance.Level, "off");
        store.Set(SettingKeys.Performance.DiagnoseUntilUtc, PerfSettingsProvider.FormatUtc(time.Now.AddHours(1)));
        var perf = new PerfSettingsProvider(store, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<PerfSettingsProvider>.Instance);
        await perf.RefreshAsync(default);

        Assert.Equal(PerfLevel.Off, perf.Level);
        Assert.False(perf.IsEnabled(PerfCollector.Http));
        Assert.False(perf.IsDiagnose(PerfCollector.Database));
    }

    [Fact]
    public async Task Collector_CanBeSwitchedOffIndividually()
    {
        var store = new InMemorySettingsService();
        store.Set(SettingKeys.Performance.CollectorFrontend, "false");
        var perf = new PerfSettingsProvider(store, new ManualTime(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PerfSettingsProvider>.Instance);
        await perf.RefreshAsync(default);

        Assert.True(perf.IsEnabled(PerfCollector.Http));
        Assert.False(perf.IsEnabled(PerfCollector.Frontend));
    }

    [Theory]
    [InlineData(SettingKeys.Performance.Level, "diagnose", false)]
    [InlineData(SettingKeys.Performance.Level, "basic", true)]
    [InlineData(SettingKeys.Performance.DiagnoseUntilUtc, "tomorrow", false)]
    [InlineData(SettingKeys.Performance.DiagnoseUntilUtc, "", true)]
    [InlineData(SettingKeys.Performance.RumSamplePercent, "150", false)]
    [InlineData(SettingKeys.Performance.Budgets, "nonsense", false)]
    [InlineData(SettingKeys.Performance.Budgets, "GET /api/tickets=500", true)]
    public void Validator_ChecksPerformanceKeys(string key, string value, bool ok)
    {
        var def = SettingDefaults.All.Single(d => d.Key == key);
        Assert.Equal(ok, SettingValueValidator.Validate(def, value) is null);
    }

    [Fact]
    public void EveryPerformanceKey_HasADefault()
    {
        var keys = typeof(SettingKeys.Performance).GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);
        foreach (var key in keys)
        {
            Assert.Contains(SettingDefaults.All, d => d.Key == key);
        }
    }
}

public sealed class BudgetTests
{
    [Fact]
    public void Parse_ReadsLinesAndSemicolons_AndSkipsInvalid()
    {
        var budgets = Budget.Parse("GET /api/tickets/{id:guid}=300\nbogus\nPOST /api/search/=400; GET /api/x=abc");
        Assert.Equal(2, budgets.Count);
        Assert.Equal("GET", budgets[0].Method);
        Assert.Equal("/api/tickets/{id:guid}", budgets[0].Route);
        Assert.Equal(300, budgets[0].TargetMs);
        Assert.Equal("/api/search", budgets[1].Route);
    }
}

public sealed class PgDeltaTests
{
    private static PgStatementSnapshotRow Row(long id, long calls, double total, DateTime? ts = null) =>
        new() { QueryId = id, Calls = calls, TotalMs = total, TsUtc = ts ?? DateTime.UtcNow };

    [Fact]
    public void Delta_SubtractsStartFromEnd()
    {
        var deltas = PgDelta.Compute(new[] { Row(1, 10, 100) }, new[] { Row(1, 25, 400) }, Array.Empty<PgStatementSnapshotRow>(), false);
        var d = Assert.Single(deltas);
        Assert.Equal(15, d.Calls);
        Assert.Equal(300, d.TotalMs);
        Assert.Equal(20, d.MeanMs);
    }

    [Fact]
    public void StatsReset_TakesEndValueAsIs()
    {
        var deltas = PgDelta.Compute(new[] { Row(1, 100, 1000) }, new[] { Row(1, 5, 50) }, Array.Empty<PgStatementSnapshotRow>(), false);
        Assert.Equal(5, Assert.Single(deltas).Calls);
    }

    [Fact]
    public void NewStatement_CountsFromFirstAppearanceInPeriod()
    {
        var deltas = PgDelta.Compute(
            new[] { Row(1, 1, 1) },
            new[] { Row(1, 1, 1), Row(2, 50, 500) },
            new[] { Row(2, 20, 200) },
            false);
        var d = Assert.Single(deltas); // statement 1 had no calls in the period
        Assert.Equal(2, d.QueryId);
        Assert.Equal(30, d.Calls);
    }
}

public sealed class HostMetricsTests
{
    private const string Stat1 = "cpu  1000 0 500 8000 100 0 0 0 0 0\ncpu0 500 0 250 4000 50 0 0 0 0 0\ncpu1 500 0 250 4000 50 0 0 0 0 0\n";
    private const string Stat2 = "cpu  1600 0 700 8400 200 0 0 100 0 0\ncpu0 1 0 0 0 0 0 0 0 0 0\ncpu1 1 0 0 0 0 0 0 0 0 0\n";

    [Fact]
    public void Cpu_PercentagesIncludeSteal()
    {
        var a = HostMetricsReader.ParseCpu(Stat1)!.Value;
        var b = HostMetricsReader.ParseCpu(Stat2)!.Value;
        var (cpu, steal, iowait) = HostMetricsReader.CpuPercentages(a, b);
        // delta total = 600+200+400+100+100 = 1400; idle+iowait = 500; steal 100
        Assert.Equal(100.0 * 900 / 1400, cpu, 3);
        Assert.Equal(100.0 * 100 / 1400, steal, 3);
        Assert.Equal(100.0 * 100 / 1400, iowait, 3);
        Assert.Equal(2, HostMetricsReader.CountCores(Stat1));
    }

    [Fact]
    public void Meminfo_Parses()
    {
        var m = HostMetricsReader.ParseMeminfo("MemTotal:       8000000 kB\nMemFree: 1 kB\nMemAvailable:   2000000 kB\nSwapTotal: 1000 kB\nSwapFree: 400 kB\n")!.Value;
        Assert.Equal(8_000_000, m.TotalKb);
        Assert.Equal(2_000_000, m.AvailableKb);
        Assert.Equal(600, m.SwapTotalKb - m.SwapFreeKb);
    }

    [Fact]
    public void Diskstats_IgnorePartitionsAndComputeRates()
    {
        const string t1 = "   8       0 sda 100 0 0 200 100 0 0 300 0 1000 0\n   8       1 sda1 99 0 0 1 99 0 0 1 0 1 0\n   7       0 loop0 5 0 0 5 0 0 0 0 0 5 0\n";
        const string t2 = "   8       0 sda 150 0 0 450 150 0 0 550 0 1600 0\n   8       1 sda1 1 0 0 1 1 0 0 1 0 1 0\n";
        var a = HostMetricsReader.ParseDiskstats(t1)!.Value;
        var b = HostMetricsReader.ParseDiskstats(t2)!.Value;
        var (awaitMs, util) = HostMetricsReader.DiskRates(a, b, elapsedMs: 1000);
        // 100 I/Os took 250+250 ms → 5 ms each; busy 600 ms of 1000 → 60 %
        Assert.Equal(5, awaitMs!.Value, 3);
        Assert.Equal(60, util!.Value, 3);
    }

    [Fact]
    public void CgroupCpuStat_Parses()
    {
        var cg = HostMetricsReader.ParseCgroupCpu("usage_usec 1\nnr_periods 200\nnr_throttled 10\nthrottled_usec 5000\n");
        Assert.Equal(((long, long)?)(200L, 10L), cg);
    }

    [Fact]
    public void Load_Parses()
    {
        Assert.Equal(1.25, HostMetricsReader.ParseLoad("1.25 0.80 0.50 2/300 12345"));
    }
}

public sealed class PlanValidationTests
{
    [Theory]
    [InlineData("SELECT * FROM tickets WHERE id = $1", true)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", true)]
    [InlineData("WITH x AS (DELETE FROM tickets RETURNING id) SELECT * FROM x", false)]
    [InlineData("UPDATE tickets SET subject = $1", false)]
    [InlineData("SELECT 1; DROP TABLE tickets", false)]
    [InlineData("SELECT * FROM tickets FOR UPDATE", false)]
    [InlineData("VACUUM tickets", false)]
    public void OnlyPlainReadsArePlanned(string sql, bool allowed)
    {
        Assert.Equal(allowed, PerfToolbox.ValidateForPlan(sql) is null);
    }
}
