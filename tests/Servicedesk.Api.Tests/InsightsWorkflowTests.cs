using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Insights.Rewind;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Workflow;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.32 — Insights Workflow: managers/admins only (on top of the Insights
/// flag), view must be a tracked view the caller can open, capped window,
/// queue-scoped, and every per-agent drill-down is audited.
public sealed class InsightsWorkflowTests
{
    private static readonly Guid QueueA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ViewId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    [Fact]
    public async Task Agent_with_insights_but_not_manager_is_refused()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStubs(factory, out _);
        var client = await ClientAsync(factory, host, "Agent", insights: true, manager: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/insights/workflow?period=week")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/insights/workflow/config")).StatusCode);
    }

    [Fact]
    public async Task Manager_without_insights_flag_is_refused()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStubs(factory, out _);
        var client = await ClientAsync(factory, host, "Agent", insights: false, manager: true);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/insights/workflow?period=week")).StatusCode);
    }

    [Fact]
    public async Task Manager_gets_a_report_scoped_to_their_queues()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStubs(factory, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true, manager: true);

        var response = await client.GetAsync($"/api/insights/workflow?period=week&viewId={ViewId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(stub.LastScope!.IsAdmin);
        Assert.Equal(new[] { QueueA }, stub.LastScope.ToArray());
        Assert.Equal(ViewId, stub.LastViewId);
    }

    [Fact]
    public async Task Untracked_or_foreign_view_is_404_and_long_windows_400()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStubs(factory, out _);
        var client = await ClientAsync(factory, host, "Admin", insights: true, manager: false);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/insights/workflow?period=week&viewId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/insights/workflow?period=year")).StatusCode);
    }

    [Fact]
    public async Task Agent_cases_are_audited_and_validated()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStubs(factory, out _);
        var client = await ClientAsync(factory, host, "Admin", insights: true, manager: false);
        var agent = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/insights/workflow/cases?period=week&agentId={agent}&kpi=Nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/insights/workflow/cases?period=week&kpi=CallBeforeMail")).StatusCode);

        var ok = await client.GetAsync($"/api/insights/workflow/cases?period=week&agentId={agent}&kpi=CallBeforeMail");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains(factory.Audit.Events, e => e.EventType == "insights.workflow.cases_viewed" && e.Target == agent.ToString());
    }

    [Fact]
    public void Local_entry_start_converts_to_utc_across_dst()
    {
        var tz = InsightsCalendar.ResolveTimeZone("Europe/Brussels");
        Assert.Equal(new DateTime(2026, 10, 8, 7, 30, 0, DateTimeKind.Utc),
            WorkflowReportService.LocalToUtc(new DateOnly(2026, 10, 8), 9 * 60 + 30, tz)); // CEST
        Assert.Equal(new DateTime(2026, 12, 8, 8, 30, 0, DateTimeKind.Utc),
            WorkflowReportService.LocalToUtc(new DateOnly(2026, 12, 8), 9 * 60 + 30, tz)); // CET
    }

    // ---- stubs -----------------------------------------------------------------------

    private sealed class StubWorkflow : IWorkflowReportService
    {
        public QueueAccessScope? LastScope { get; private set; }
        public Guid? LastViewId { get; private set; }
        private static readonly WorkflowLimits Limits = new(15, 60, 15, 15, 60, 2, Array.Empty<Guid>());

        public Task<WorkflowLimits> GetLimitsAsync(CancellationToken ct = default) => Task.FromResult(Limits);

        public Task<WorkflowReport> GetReportAsync(InsightsRange range, Guid? viewId, QueueAccessScope scope, CancellationToken ct = default)
        {
            LastScope = scope;
            LastViewId = viewId;
            return Task.FromResult(new WorkflowReport(range, viewId, Limits, Array.Empty<WorkflowAgentRow>()));
        }

        public Task<IReadOnlyList<WorkflowCase>> GetCasesAsync(InsightsRange range, Guid? viewId, QueueAccessScope scope,
            Guid agentId, WorkflowKpi kpi, CancellationToken ct = default)
        {
            LastScope = scope;
            return Task.FromResult<IReadOnlyList<WorkflowCase>>(Array.Empty<WorkflowCase>());
        }
    }

    private sealed class StubRewind : IRewindService
    {
        public Task<IReadOnlyList<RewindViewSummary>> ListViewsAsync(Guid userId, string role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RewindViewSummary>>(new[] { new RewindViewSummary(ViewId, "Servicedesk") });
        public Task<bool> CanReadViewAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default)
            => Task.FromResult(viewId == ViewId);
        public Task<int> GetIntervalAsync(CancellationToken ct = default) => Task.FromResult(15);
        public Task<RewindSeries> GetSeriesAsync(Guid viewId, DateTime? endUtc, TimeSpan span, QueueAccessScope scope, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RewindSnapshot> GetSnapshotAsync(Guid viewId, DateTime atUtc, QueueAccessScope scope, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ClockOnlyInsights : IInsightsService
    {
        public Task<InsightsClock> GetClockAsync(CancellationToken ct = default)
            => Task.FromResult(new InsightsClock(TimeZoneInfo.Utc, new DateOnly(2026, 10, 8)));
        public Task<TicketCountReport> GetTicketCountsAsync(InsightsTicketFilter filter, InsightsRange range,
            InsightsGranularity granularity, QueueAccessScope scope, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<InsightsTicketPage> ListTicketsAsync(InsightsTicketFilter filter, InsightsRange range,
            QueueAccessScope scope, IReadOnlyCollection<Guid>? queueIds, int offset, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class OnlyQueueA : IQueueAccessService
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

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithStubs(
        SecurityBaselineFactory factory, out StubWorkflow stub)
    {
        var s = new StubWorkflow();
        stub = s;
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkflowReportService>();
            services.AddSingleton<IWorkflowReportService>(s);
            services.RemoveAll<IRewindService>();
            services.AddSingleton<IRewindService>(new StubRewind());
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton<IQueueAccessService>(new OnlyQueueA());
            services.RemoveAll<IInsightsService>();
            services.AddSingleton<IInsightsService>(new ClockOnlyInsights());
        }));
    }

    private static async Task<HttpClient> ClientAsync(
        SecurityBaselineFactory factory, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        string role, bool insights, bool manager)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        if (insights) factory.Users.InsightsEnabled[userId] = true;
        if (manager) factory.Users.TimesheetManagers[userId] = true;
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
}
