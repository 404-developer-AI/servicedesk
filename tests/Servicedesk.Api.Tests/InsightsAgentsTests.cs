using System.Net;
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

/// v0.1.14 — Insights Agents overview: agent-selection validation, the
/// feature-flag gate, queue-access scoping, the grid fold and the PDF.
public sealed class InsightsAgentsTests
{
    private static readonly Guid QueueA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentOne = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid AgentTwo = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid AgentThree = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid AgentFour = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");

    // ---- endpoint gate -----------------------------------------------------

    [Theory]
    [InlineData("/api/insights/agents?period=month&agentIds=bbbbbbbb-0000-0000-0000-000000000001")]
    [InlineData("/api/insights/agents/tickets?period=month&agentId=bbbbbbbb-0000-0000-0000-000000000001")]
    [InlineData("/api/insights/agents/pdf?period=month&agentIds=bbbbbbbb-0000-0000-0000-000000000001")]
    [InlineData("/api/insights/agents/opened?period=month&agentId=bbbbbbbb-0000-0000-0000-000000000001")]
    public async Task Agent_without_flag_gets_403(string url)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: false);

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(stub.LastScope);
    }

    [Fact]
    public async Task Customer_is_refused_even_with_flag()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var client = await ClientAsync(factory, host, "Customer", insights: true);

        var response = await client.GetAsync($"/api/insights/agents?period=month&agentIds={AgentOne}");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    // ---- selection validation ----------------------------------------------

    [Fact]
    public async Task No_agents_is_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync("/api/insights/agents?period=month");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task More_agents_than_the_compare_max_is_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/agents?period=month&agentIds={AgentOne}&agentIds={AgentTwo}&agentIds={AgentThree}&agentIds={AgentFour}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.LastScope);
    }

    [Fact]
    public async Task Duplicate_ids_count_once()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/agents?period=month&agentIds={AgentOne}&agentIds={AgentOne}&agentIds={AgentOne}&agentIds={AgentTwo}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { AgentOne, AgentTwo }, stub.LastAgents);
    }

    [Fact]
    public async Task A_non_staff_or_unknown_id_fails_the_whole_request()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        // The stub only knows AgentOne/Two/Three — anything else is a customer
        // or does not exist, and must not shrink the comparison silently.
        var response = await client.GetAsync(
            $"/api/insights/agents?period=month&agentIds={AgentOne}&agentIds={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.LastScope);
    }

    // ---- scoping + paging --------------------------------------------------

    [Fact]
    public async Task Report_is_scoped_to_the_callers_queues()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/agents?period=month&agentIds={AgentOne}&agentIds={AgentTwo}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(stub.LastScope!.IsAdmin);
        Assert.Equal(new[] { QueueA }, stub.LastScope.ToArray());
    }

    [Fact]
    public async Task Ticket_list_is_scoped_sorted_and_clamped()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/agents/tickets?period=week&agentId={AgentTwo}&sort=period&listOffset=-3&limit=9999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { QueueA }, stub.LastScope!.ToArray());
        Assert.Equal(AgentTwo, stub.LastListAgent);
        Assert.Equal(AgentTicketSort.PeriodTime, stub.LastSort);
        Assert.Equal(0, stub.LastOffset);
        Assert.Equal(200, stub.LastLimit);
    }

    [Theory]
    [InlineData("sort=newest")]
    [InlineData("sort=period;drop")]
    public async Task Unknown_sort_is_400(string qs)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/agents/tickets?period=week&agentId={AgentOne}&{qs}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_list_for_a_non_agent_is_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/agents/tickets?period=week&agentId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.LastListAgent);
    }

    [Fact]
    public async Task Pdf_is_audited_and_lists_are_capped()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/agents/pdf?period=month&agentIds={AgentOne}&agentIds={AgentTwo}&metric=time");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(InsightsEndpoints.PdfTicketCap, stub.LastLimit);
        Assert.Contains(factory.Audit.Events, e => e.EventType == "insights.report.pdf_exported" && e.Target == "agents");
    }

    [Fact]
    public async Task Opened_list_is_scoped_clamped_and_uses_the_min_duration_setting()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/agents/opened?period=week&agentId={AgentThree}&listOffset=-1&limit=5000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { QueueA }, stub.LastScope!.ToArray());
        Assert.Equal(AgentThree, stub.LastListAgent);
        Assert.Equal(3, stub.LastMinSeconds); // default Insights.OpenedNoActionMinSeconds
        Assert.Equal(0, stub.LastOffset);
        Assert.Equal(200, stub.LastLimit);
    }

    [Fact]
    public async Task Opened_list_for_a_non_agent_is_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/agents/opened?period=week&agentId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, stub.OpenedCalls);
    }

    [Fact]
    public async Task Pdf_includes_an_opened_list_per_agent()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync(
            $"/api/insights/agents/pdf?period=month&agentIds={AgentOne}&agentIds={AgentTwo}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.OpenedCalls);
    }

    [Fact]
    public async Task Pdf_with_unknown_metric_is_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, out _);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/agents/pdf?period=month&agentIds={AgentOne}&metric=revenue");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- pure pieces -------------------------------------------------------

    [Fact]
    public void Assemble_uses_the_grouping_set_total_not_the_bucket_sum()
    {
        var range = new InsightsRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3));
        var buckets = InsightsCalendar.BuildBuckets(range, InsightsGranularity.Day);
        var agents = new[] { Agent(AgentOne, 0), Agent(AgentTwo, 1) };

        var report = AgentInsightsService.Assemble(agents, range, InsightsGranularity.Day,
            new InsightsClock(TimeZoneInfo.Utc, new DateOnly(2026, 9, 3)), buckets,
            new[]
            {
                // Same ticket on day 1 and day 2 → 1 + 1 in the buckets, 1 overall.
                new AgentInsightsService.TicketActivityRow { UserId = AgentOne, Bucket = 1, Tickets = 1, Minutes = 30 },
                new AgentInsightsService.TicketActivityRow { UserId = AgentOne, Bucket = 2, Tickets = 1, Minutes = 15 },
                new AgentInsightsService.TicketActivityRow { UserId = AgentOne, Bucket = null, Tickets = 1, Minutes = 45 },
                // A user that was not requested is ignored.
                new AgentInsightsService.TicketActivityRow { UserId = AgentFour, Bucket = null, Tickets = 9, Minutes = 900 },
            },
            new[]
            {
                new AgentInsightsService.CallRow { UserId = AgentTwo, Bucket = 3, Calls = 3, CallsIn = 2, CallsOut = 1, CallSeconds = 400 },
                new AgentInsightsService.CallRow { UserId = AgentTwo, Bucket = null, Calls = 3, CallsIn = 2, CallsOut = 1, CallSeconds = 400 },
            });

        Assert.Equal(1, report.Totals[0].Tickets);
        Assert.Equal(45, report.Totals[0].TicketMinutes);
        Assert.Equal(1, report.Buckets[0].Values[0].Tickets);
        Assert.Equal(1, report.Buckets[1].Values[0].Tickets);
        Assert.Equal(0, report.Totals[1].Tickets);
        Assert.Equal(2, report.Totals[1].CallsIn);
        Assert.Equal(1, report.Buckets[2].Values[1].CallsOut);
        Assert.Equal(400, report.Totals[1].CallSeconds);
    }

    [Theory]
    [InlineData(AgentTicketSort.Recent)]
    [InlineData(AgentTicketSort.PeriodTime)]
    [InlineData(AgentTicketSort.AgentTime)]
    [InlineData(AgentTicketSort.TicketTime)]
    public void Order_by_is_a_fixed_fragment(AgentTicketSort sort)
    {
        var sql = AgentInsightsService.OrderBy(sort);
        Assert.DoesNotContain("@", sql);
        Assert.EndsWith("DESC", sql);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("1", true)]
    [InlineData("6", true)]
    [InlineData("0", false)]
    [InlineData("7", false)]
    [InlineData("-1", false)]
    [InlineData("three", false)]
    public void Compare_max_setting_is_validated(string value, bool ok)
    {
        var def = SettingDefaults.All.Single(d => d.Key == SettingKeys.Insights.AgentCompareMax);
        Assert.Equal(ok, SettingValueValidator.Validate(def, value) is null);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("0", true)]
    [InlineData("3600", true)]
    [InlineData("-1", false)]
    [InlineData("3601", false)]
    [InlineData("2.5", false)]
    public void Opened_min_seconds_setting_is_validated(string value, bool ok)
    {
        var def = SettingDefaults.All.Single(d => d.Key == SettingKeys.Insights.OpenedNoActionMinSeconds);
        Assert.Equal(ok, SettingValueValidator.Validate(def, value) is null);
    }

    [Fact]
    public void Ticket_open_session_retention_has_a_default()
    {
        var def = SettingDefaults.All.Single(d => d.Key == SettingKeys.Retention.TicketOpenSessionsDays);
        Assert.Equal("365", def.Value);
    }

    [Fact]
    public void Assemble_folds_opened_without_action_counts()
    {
        var range = new InsightsRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2));
        var buckets = InsightsCalendar.BuildBuckets(range, InsightsGranularity.Day);
        var report = AgentInsightsService.Assemble(new[] { Agent(AgentOne, 0) }, range, InsightsGranularity.Day,
            new InsightsClock(TimeZoneInfo.Utc, new DateOnly(2026, 9, 2)), buckets,
            Array.Empty<AgentInsightsService.TicketActivityRow>(),
            Array.Empty<AgentInsightsService.CallRow>(),
            new[]
            {
                new AgentInsightsService.OpenedRow { UserId = AgentOne, Bucket = 2, Count = 4 },
                new AgentInsightsService.OpenedRow { UserId = AgentOne, Bucket = null, Count = 4 },
            });

        Assert.Equal(4, report.Totals[0].OpenedNoAction);
        Assert.Equal(0, report.Buckets[0].Values[0].OpenedNoAction);
        Assert.Equal(4, report.Buckets[1].Values[0].OpenedNoAction);
    }

    [Theory]
    [InlineData(4, "4s")]
    [InlineData(95, "1m 35s")]
    [InlineData(5400, "1h 30m")]
    [InlineData(93600, "1d 2h")]
    public void Open_duration_format(int seconds, string expected) =>
        Assert.Equal(expected, AgentActivityPdfGenerator.FormatDuration(seconds));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45m")]
    [InlineData(180, "3h")]
    [InlineData(750, "12h 30m")]
    public void Minutes_format(int minutes, string expected) =>
        Assert.Equal(expected, AgentActivityPdfGenerator.FormatMinutes(minutes));

    [Fact]
    public void Pdf_renders_and_chart_never_contains_agent_names()
    {
        var report = SampleReport() with
        {
            Agents = new[]
            {
                new InsightsAgent(AgentOne, "<script>alert(1)</script>", "one@example.test", "Agent", 0),
                new InsightsAgent(AgentTwo, "Jamie Doe", "two@example.test", "Admin", 1),
            },
        };
        foreach (var metric in Enum.GetValues<AgentChartMetric>())
        {
            var svg = AgentActivityPdfGenerator.BuildChartSvg(report, metric, new[] { "#2a78d6", "#eb6834" }, 600, 200);
            Assert.DoesNotContain("script", svg);
            Assert.DoesNotContain("Jamie", svg);
        }

        var lists = report.Agents.Select(a => new AgentPdfTicketList(a, new AgentTicketPage(1, new[]
        {
            new AgentTicketItem(Guid.NewGuid(), 1042, "Printer offline", "Sam", "Acme", "Open", "#3b82f6", "Open",
                DateTime.UtcNow, new DateOnly(2026, 9, 2), 30, 90, 120),
        }), new OpenedNoActionPage(2, new[]
        {
            new OpenedNoActionItem(1, Guid.NewGuid(), 1043, "VPN down", "Acme", "Open", "#3b82f6", "Open",
                DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow, "removed", 180),
            new OpenedNoActionItem(2, Guid.NewGuid(), 1044, "New laptop", null, "Closed", "#6b7280", "Closed",
                DateTime.UtcNow.AddDays(-2), DateTime.UtcNow, "evicted", 172800),
        }))).ToList();
        var bytes = AgentActivityPdfGenerator.Generate(new AgentActivityPdfData(
            report, AgentChartMetric.Tickets, lists, DateTime.UtcNow, "agent@example.test"));
        Assert.Equal("%PDF", global::System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    // ---- plumbing ----------------------------------------------------------

    private static InsightsAgent Agent(Guid id, int slot) => new(id, $"Agent {slot}", $"a{slot}@example.test", "Agent", slot);

    private static AgentActivityReport SampleReport()
    {
        var range = new InsightsRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var buckets = InsightsCalendar.BuildBuckets(range, InsightsGranularity.Week);
        var agents = new[] { Agent(AgentOne, 0), Agent(AgentTwo, 1) };
        var values = new AgentActivityTotals[] { new(4, 150, 3, 2, 1, 600), new(2, 45, 0, 0, 0, 0) };
        return new AgentActivityReport(range, InsightsGranularity.Week, "UTC", new DateOnly(2026, 9, 10), agents,
            values,
            buckets.Select(b => new AgentActivityBucket(b.Start, b.From, b.To, values)).ToList());
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithStub(
        SecurityBaselineFactory factory, out StubAgentInsights stub)
    {
        var s = new StubAgentInsights();
        stub = s;
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentInsightsService>();
            services.AddSingleton<IAgentInsightsService>(s);
            services.RemoveAll<IInsightsService>();
            services.AddSingleton<IInsightsService>(new StubClock());
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton<IQueueAccessService>(new StubQueueAccess());
        }));
    }

    private static async Task<HttpClient> ClientAsync(
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
        return client;
    }

    private sealed class StubAgentInsights : IAgentInsightsService
    {
        private static readonly Guid[] Known = { AgentOne, AgentTwo, AgentThree, AgentFour };

        public QueueAccessScope? LastScope { get; private set; }
        public Guid[]? LastAgents { get; private set; }
        public Guid? LastListAgent { get; private set; }
        public AgentTicketSort? LastSort { get; private set; }
        public int LastOffset { get; private set; }
        public int LastLimit { get; private set; }
        public int? LastMinSeconds { get; private set; }
        public int OpenedCalls { get; private set; }

        public Task<OpenedNoActionPage> ListOpenedWithoutActionAsync(
            Guid agentId, InsightsRange range, InsightsClock clock, QueueAccessScope scope,
            int minSeconds, int offset, int limit, CancellationToken ct = default)
        {
            OpenedCalls++;
            LastScope = scope;
            LastListAgent = agentId;
            LastMinSeconds = minSeconds;
            LastOffset = offset;
            LastLimit = limit;
            return Task.FromResult(new OpenedNoActionPage(0, Array.Empty<OpenedNoActionItem>()));
        }

        public Task<IReadOnlyList<InsightsAgent>> ResolveAgentsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
        {
            var result = ids.Distinct().Where(Known.Contains).Select((id, i) => Agent(id, i)).ToList();
            return Task.FromResult<IReadOnlyList<InsightsAgent>>(result);
        }

        public Task<AgentActivityReport> GetActivityAsync(
            IReadOnlyList<InsightsAgent> agents, InsightsRange range, InsightsGranularity granularity,
            InsightsClock clock, QueueAccessScope scope, int minSeconds, CancellationToken ct = default)
        {
            LastScope = scope;
            LastMinSeconds = minSeconds;
            LastAgents = agents.Select(a => a.Id).ToArray();
            var buckets = InsightsCalendar.BuildBuckets(range, granularity);
            var empty = agents.Select(_ => AgentActivityTotals.Empty).ToList();
            return Task.FromResult(new AgentActivityReport(range, granularity, "UTC", clock.Today, agents, empty,
                buckets.Select(b => new AgentActivityBucket(b.Start, b.From, b.To, empty)).ToList()));
        }

        public Task<AgentTicketPage> ListTicketsAsync(
            Guid agentId, InsightsRange range, InsightsClock clock, QueueAccessScope scope,
            AgentTicketSort sort, int offset, int limit, CancellationToken ct = default)
        {
            LastScope = scope;
            LastListAgent = agentId;
            LastSort = sort;
            LastOffset = offset;
            LastLimit = limit;
            return Task.FromResult(new AgentTicketPage(0, Array.Empty<AgentTicketItem>()));
        }
    }

    private sealed class StubClock : IInsightsService
    {
        public Task<InsightsClock> GetClockAsync(CancellationToken ct = default)
            => Task.FromResult(new InsightsClock(TimeZoneInfo.Utc, new DateOnly(2026, 9, 10)));

        public Task<TicketCountReport> GetTicketCountsAsync(
            InsightsTicketFilter filter, InsightsRange range, InsightsGranularity granularity,
            QueueAccessScope scope, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<InsightsTicketPage> ListTicketsAsync(
            InsightsTicketFilter filter, InsightsRange range, QueueAccessScope scope,
            IReadOnlyCollection<Guid>? queueIds, int offset, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
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
