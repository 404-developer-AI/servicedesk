using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Insights;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.13 — Insights reporting dashboard: calendar math, the per-user
/// feature-flag gate, queue-access scoping and the PDF export.
public sealed class InsightsTests
{
    private static readonly Guid QueueA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid QueueB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    // ---- calendar ----------------------------------------------------------

    [Fact]
    public void Week_period_runs_monday_to_sunday()
    {
        var today = new DateOnly(2026, 9, 30); // Wednesday
        var range = InsightsCalendar.ResolvePeriod(InsightsPeriod.Week, 0, today);
        Assert.Equal(new DateOnly(2026, 9, 28), range.From);
        Assert.Equal(new DateOnly(2026, 10, 4), range.To);

        var previous = InsightsCalendar.ResolvePeriod(InsightsPeriod.Week, -1, today);
        Assert.Equal(new DateOnly(2026, 9, 21), previous.From);
    }

    [Fact]
    public void Month_and_year_periods_cover_the_full_calendar_period()
    {
        var today = new DateOnly(2026, 2, 10);
        var month = InsightsCalendar.ResolvePeriod(InsightsPeriod.Month, 0, today);
        Assert.Equal(new InsightsRange(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), month);

        var lastYear = InsightsCalendar.ResolvePeriod(InsightsPeriod.Year, -1, today);
        Assert.Equal(new InsightsRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), lastYear);
    }

    [Fact]
    public void Future_offsets_are_clamped_to_the_current_period()
    {
        var today = new DateOnly(2026, 9, 30);
        Assert.Equal(
            InsightsCalendar.ResolvePeriod(InsightsPeriod.Month, 0, today),
            InsightsCalendar.ResolvePeriod(InsightsPeriod.Month, 5, today));
    }

    [Fact]
    public void Weekly_buckets_clip_partial_weeks_to_the_range()
    {
        var range = new InsightsRange(new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 16)); // Wed → Wed
        var buckets = InsightsCalendar.BuildBuckets(range, InsightsGranularity.Week);

        Assert.Equal(3, buckets.Count);
        Assert.Equal(InsightsCalendar.CountBuckets(range, InsightsGranularity.Week), buckets.Count);
        Assert.Equal(new InsightsBucket(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 6)), buckets[0]);
        Assert.Equal(new InsightsBucket(new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16)), buckets[2]);
    }

    [Theory]
    [InlineData(InsightsGranularity.Day)]
    [InlineData(InsightsGranularity.Week)]
    [InlineData(InsightsGranularity.Month)]
    [InlineData(InsightsGranularity.Year)]
    public void Bucket_count_matches_materialised_buckets(InsightsGranularity g)
    {
        var range = new InsightsRange(new DateOnly(2024, 12, 30), new DateOnly(2026, 3, 3));
        Assert.Equal(InsightsCalendar.BuildBuckets(range, g).Count, InsightsCalendar.CountBuckets(range, g));
    }

    [Fact]
    public void Bucket_bounds_follow_local_midnight_across_dst()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels");
        var range = new InsightsRange(new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 30));
        var bounds = InsightsCalendar.BucketBoundsUtc(InsightsCalendar.BuildBuckets(range, InsightsGranularity.Day), tz);

        Assert.Equal(new DateTime(2026, 3, 27, 23, 0, 0, DateTimeKind.Utc), bounds[0]); // CET, UTC+1
        Assert.Equal(new DateTime(2026, 3, 29, 22, 0, 0, DateTimeKind.Utc), bounds[2]); // CEST, UTC+2
    }

    [Fact]
    public void Default_granularity_scales_with_range_length()
    {
        Assert.Equal(InsightsGranularity.Day,
            InsightsCalendar.DefaultGranularity(new InsightsRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))));
        Assert.Equal(InsightsGranularity.Month,
            InsightsCalendar.DefaultGranularity(new InsightsRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31))));
    }

    // ---- query validation --------------------------------------------------

    [Theory]
    [InlineData("custom", null, "2026-09-30", null)]          // missing from
    [InlineData("custom", "2026-10-01", "2026-09-01", null)]  // inverted
    [InlineData("custom", "1990-01-01", "2026-09-01", null)]  // before 2000
    [InlineData("custom", "2016-01-01", "2026-09-01", "day")] // > MaxBuckets
    [InlineData("fortnight", null, null, null)]               // unknown period
    [InlineData("month", null, null, "3")]                    // numeric enum smuggling
    public void Invalid_queries_are_rejected(string period, string? from, string? to, string? granularity)
    {
        var ok = InsightsEndpoints.TryResolve(
            new InsightsEndpoints.ReportQuery(period, null, from, to, granularity),
            new DateOnly(2026, 9, 30), out _, out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Custom_range_with_explicit_granularity_is_accepted()
    {
        var ok = InsightsEndpoints.TryResolve(
            new InsightsEndpoints.ReportQuery("custom", null, "2026-01-01", "2026-06-30", "week"),
            new DateOnly(2026, 9, 30), out var range, out var granularity, out _);
        Assert.True(ok);
        Assert.Equal(new DateOnly(2026, 1, 1), range.From);
        Assert.Equal(InsightsGranularity.Week, granularity);
    }

    // ---- summary + pdf -----------------------------------------------------

    [Fact]
    public void Summary_averages_over_elapsed_days_only()
    {
        var report = SampleReport(new DateOnly(2026, 9, 10));
        var s = TicketCountSummary.Compute(report);

        Assert.Equal(12, s.Total);
        Assert.Equal(10, s.ElapsedDays);
        Assert.Equal(1.2, s.AveragePerDay, 3);
        Assert.Equal("Servicedesk", s.TopQueue!.Name);
        Assert.Equal(7, s.PeakCount);
    }

    [Fact]
    public void Pdf_renders_a_document()
    {
        var bytes = TicketCountPdfGenerator.Generate(new TicketCountPdfData(
            SampleReport(new DateOnly(2026, 9, 10)), AllQueues: true, DateTime.UtcNow, "agent@example.test"));
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", global::System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Chart_svg_never_contains_queue_names()
    {
        var report = SampleReport(new DateOnly(2026, 9, 10)) with
        {
            Queues = new[]
            {
                new InsightsQueue(QueueA, "<script>alert(1)</script>", true, 0),
                new InsightsQueue(QueueB, "Back office", true, 1),
            },
        };
        var svg = TicketCountPdfGenerator.BuildChartSvg(report, new[] { "#2a78d6", "#eb6834" }, 600, 200);
        Assert.DoesNotContain("script", svg);
        Assert.DoesNotContain("Back office", svg);
    }

    // ---- endpoint gate -----------------------------------------------------

    [Theory]
    [InlineData("/api/insights/config")]
    [InlineData("/api/insights/new-tickets?period=month")]
    [InlineData("/api/insights/new-tickets/pdf?period=month")]
    [InlineData("/api/insights/new-tickets/tickets?period=month")]
    [InlineData("/api/insights/closed-tickets?period=month")]
    [InlineData("/api/insights/closed-tickets/pdf?period=month")]
    [InlineData("/api/insights/closed-tickets/tickets?period=month")]
    public async Task Agent_without_flag_gets_403(string url)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: false);

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/insights/config")]
    [InlineData("/api/insights/new-tickets?period=month")]
    public async Task Customer_is_refused_even_with_flag(string url)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Customer", insights: true);

        var response = await client.GetAsync(url);

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"{url} → {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Agent_with_flag_gets_report_scoped_to_their_queues()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/new-tickets?period=month");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(stub.LastScope);
        Assert.False(stub.LastScope!.IsAdmin);
        Assert.Equal(new[] { QueueA }, stub.LastScope.ToArray());
    }

    [Fact]
    public async Task Pdf_with_only_foreign_queue_ids_is_rejected()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/new-tickets/pdf?period=month&queueIds={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_export_returns_pdf_and_is_audited()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/new-tickets/pdf?period=month&queueIds={QueueA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(factory.Audit.Events, e => e.EventType == "insights.report.pdf_exported");
    }

    [Fact]
    public async Task Ticket_list_is_access_scoped_and_page_size_clamped()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/new-tickets/tickets?period=today&queueIds={QueueB}&listOffset=-5&limit=5000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(stub.LastScope!.IsAdmin);
        Assert.Equal(new[] { QueueA }, stub.LastScope.ToArray());
        Assert.Equal(new[] { QueueB }, stub.LastQueueIds);
        Assert.Equal(0, stub.LastOffset);
        Assert.Equal(200, stub.LastLimit);
    }

    // ---- closed-tickets overview -------------------------------------------

    [Theory]
    [InlineData(null, ClosedOutcomes.All)]
    [InlineData("resolved", ClosedOutcomes.Resolved)]
    [InlineData("closed, merged", ClosedOutcomes.Closed | ClosedOutcomes.Merged)]
    [InlineData("RESOLVED,closed,merged", ClosedOutcomes.All)]
    public void Closed_outcomes_parse(string? raw, ClosedOutcomes expected)
    {
        Assert.True(InsightsEndpoints.TryResolveFilter("closed-tickets", raw, out var filter, out _));
        Assert.Equal(InsightsMetric.Closed, filter.Metric);
        Assert.Equal(expected, filter.Outcomes);
    }

    [Theory]
    [InlineData("reopened")]
    [InlineData("resolved;drop table")]
    [InlineData(",,")]
    public void Invalid_outcomes_are_rejected(string raw)
    {
        Assert.False(InsightsEndpoints.TryResolveFilter("closed-tickets", raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void New_tickets_ignore_outcomes()
    {
        Assert.True(InsightsEndpoints.TryResolveFilter("new-tickets", "garbage", out var filter, out _));
        Assert.Equal(InsightsMetric.New, filter.Metric);
    }

    [Fact]
    public void Closed_fragments_use_close_moment_and_outcome_predicates()
    {
        var (moment, where) = InsightsService.Fragments(new InsightsTicketFilter(InsightsMetric.Closed, ClosedOutcomes.Merged));
        Assert.Equal("COALESCE(t.closed_utc, t.resolved_utc)", moment);
        Assert.Contains("s.state_category IN ('Resolved', 'Closed')", where);
        Assert.Contains("t.merged_into_ticket_id IS NOT NULL", where);
        Assert.DoesNotContain("'Resolved' AND", where);

        var (newMoment, newWhere) = InsightsService.Fragments(InsightsTicketFilter.NewTickets);
        Assert.Equal("t.created_utc", newMoment);
        Assert.Equal("", newWhere);
    }

    [Fact]
    public async Task Closed_report_passes_outcomes_and_scope()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/closed-tickets?period=month&outcomes=resolved,merged");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new InsightsTicketFilter(InsightsMetric.Closed, ClosedOutcomes.Resolved | ClosedOutcomes.Merged), stub.LastFilter);
        Assert.Equal(new[] { QueueA }, stub.LastScope!.ToArray());
    }

    [Fact]
    public async Task Closed_pdf_is_audited_with_its_kind()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/closed-tickets/pdf?period=month");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(factory.Audit.Events, e => e.EventType == "insights.report.pdf_exported" && e.Target == "closed-tickets");
    }

    [Fact]
    public async Task Unknown_report_kind_is_404()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/deleted-tickets?period=month");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Empty_outcome_selection_returns_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/closed-tickets?period=month&outcomes=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Closed_pdf_renders()
    {
        var report = SampleReport(new DateOnly(2026, 9, 10)) with
        {
            Filter = new InsightsTicketFilter(InsightsMetric.Closed, ClosedOutcomes.Resolved),
        };
        var bytes = TicketCountPdfGenerator.Generate(new TicketCountPdfData(report, true, DateTime.UtcNow, "agent@example.test"));
        Assert.Equal("%PDF", global::System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task Bad_query_returns_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var (client, _) = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/new-tickets?period=custom&from=2026-10-01&to=2026-09-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- plumbing ----------------------------------------------------------

    private static TicketCountReport SampleReport(DateOnly today)
    {
        var range = new InsightsRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var buckets = InsightsCalendar.BuildBuckets(range, InsightsGranularity.Week)
            .Select((b, i) => new TicketCountBucket(b.Start, b.From, b.To, i == 1 ? new[] { 5, 2 } : i == 2 ? new[] { 3, 2 } : new[] { 0, 0 }))
            .ToList();
        return new TicketCountReport(InsightsTicketFilter.NewTickets, range, InsightsGranularity.Week, "UTC", today,
            new[] { new InsightsQueue(QueueA, "Servicedesk", true, 0), new InsightsQueue(QueueB, "Back office", true, 1) },
            buckets);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithStub(
        SecurityBaselineFactory factory, out StubInsightsService stub)
    {
        var s = new StubInsightsService();
        stub = s;
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInsightsService>();
            services.AddSingleton<IInsightsService>(s);
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton<IQueueAccessService>(new StubQueueAccess());
        }));
    }

    private static async Task<(HttpClient Client, Guid UserId)> ClientAsync(
        SecurityBaselineFactory factory,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        string role, bool insights)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        if (insights) factory.Users.InsightsEnabled[userId] = true;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return (client, userId);
    }

    private sealed class StubInsightsService : IInsightsService
    {
        public QueueAccessScope? LastScope { get; private set; }
        public Guid[]? LastQueueIds { get; private set; }
        public int LastOffset { get; private set; }
        public int LastLimit { get; private set; }

        public InsightsTicketFilter? LastFilter { get; private set; }

        public Task<InsightsTicketPage> ListTicketsAsync(
            InsightsTicketFilter filter, InsightsRange range, QueueAccessScope scope,
            IReadOnlyCollection<Guid>? queueIds, int offset, int limit, CancellationToken ct = default)
        {
            LastFilter = filter;
            LastScope = scope;
            LastQueueIds = queueIds?.ToArray();
            LastOffset = offset;
            LastLimit = limit;
            return Task.FromResult(new InsightsTicketPage(0, Array.Empty<InsightsTicketItem>()));
        }

        public Task<InsightsClock> GetClockAsync(CancellationToken ct = default)
            => Task.FromResult(new InsightsClock(TimeZoneInfo.Utc, new DateOnly(2026, 9, 10)));

        public Task<TicketCountReport> GetTicketCountsAsync(
            InsightsTicketFilter filter, InsightsRange range, InsightsGranularity granularity,
            QueueAccessScope scope, CancellationToken ct = default)
        {
            LastFilter = filter;
            LastScope = scope;
            var report = SampleReport(new DateOnly(2026, 9, 10)) with { Filter = filter };
            // Emulate the SQL access filter: only visible queues come back.
            var keep = report.Queues.Select((q, i) => (q, i)).Where(x => scope.CanSee(x.q.Id)).ToList();
            return Task.FromResult(report with
            {
                Queues = keep.Select(x => x.q).ToList(),
                Buckets = report.Buckets.Select(b => b with { Counts = keep.Select(x => b.Counts[x.i]).ToArray() }).ToList(),
            });
        }
    }

    private sealed class StubQueueAccess : IQueueAccessService
    {
        public Task<IReadOnlyList<Guid>> GetAccessibleQueueIdsAsync(Guid userId, string role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(new[] { QueueA });

        public Task<bool> HasQueueAccessAsync(Guid userId, string role, Guid queueId, CancellationToken ct = default)
            => Task.FromResult(queueId == QueueA);

        public Task SetQueueAccessAsync(Guid userId, IReadOnlyList<Guid> queueIds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> GetUsersForQueueAsync(Guid queueId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public void InvalidateCache(Guid userId) { }
    }
}
