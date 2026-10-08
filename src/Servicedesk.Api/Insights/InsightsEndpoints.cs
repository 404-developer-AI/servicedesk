using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Insights.Rewind;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Workflow;

namespace Servicedesk.Api.Insights;

/// Insights — the reporting dashboard (v0.1.13). Agent/Admin role-gated at
/// the policy and gated per user by <c>users.insights_enabled</c>, checked
/// in-handler (Forbid on miss) so the flag is the real security boundary.
/// Every figure is additionally scoped to the caller's queue access: an
/// agent never sees counts or names of queues they cannot open.
///
/// Two ticket-count overviews share one set of handlers, selected by the
/// route segment: <c>new-tickets</c> (counted on creation) and
/// <c>closed-tickets</c> (counted on the close moment, with an optional
/// <c>outcomes=resolved,closed,merged</c> filter).
///
/// v0.1.14 adds the Agents overview under <c>/agents</c>: one to
/// <c>Insights.AgentCompareMax</c> agents side by side — tickets worked on,
/// time on tickets, calls — plus each agent's ticket list and a PDF.
///
/// v0.1.31 adds Rewind under <c>/rewind</c>: quarter-hour snapshots of the
/// views an admin marked as tracked. Besides the flag, every call checks
/// view access (404 when missing, like <c>GET /api/views/{id}</c>) and cuts
/// tickets and counts to the caller's queue access.
public static class InsightsEndpoints
{
    private const string PdfExportedEvent = "insights.report.pdf_exported";
    private const string KindRoute = "/{kind:regex(^(new|closed)-tickets$)}";

    /// Page size of the ticket list under a report (and its hard cap).
    private const int ListPageSize = 50;
    private const int ListMaxPageSize = 200;

    /// Per-agent ticket rows in the Agents PDF — a guard against a
    /// multi-thousand-page document, not a tunable. The PDF says when a
    /// list was cut.
    internal const int PdfTicketCap = 500;

    /// Absolute bounds for Insights.AgentCompareMax (the validator agrees).
    private const int CompareMaxFloor = 1;
    private const int CompareMaxCeiling = 6;

    public static IEndpointRouteBuilder MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/insights")
            .WithTags("Insights")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/config", GetConfig).WithName("InsightsConfig").WithOpenApi();
        group.MapGet(KindRoute, GetReport).WithName("InsightsReport").WithOpenApi();
        group.MapGet(KindRoute + "/pdf", ExportPdf).WithName("InsightsReportPdf").WithOpenApi();
        group.MapGet(KindRoute + "/tickets", ListTickets).WithName("InsightsReportTickets").WithOpenApi();
        group.MapGet("/agents", GetAgentReport).WithName("InsightsAgents").WithOpenApi();
        group.MapGet("/agents/tickets", ListAgentTickets).WithName("InsightsAgentTickets").WithOpenApi();
        group.MapGet("/agents/opened", ListOpenedWithoutAction).WithName("InsightsAgentOpened").WithOpenApi();
        group.MapGet("/agents/pdf", ExportAgentPdf).WithName("InsightsAgentsPdf").WithOpenApi();
        group.MapGet("/rewind/views", ListRewindViews).WithName("InsightsRewindViews").WithOpenApi();
        group.MapGet("/rewind/{viewId:guid}/series", GetRewindSeries).WithName("InsightsRewindSeries").WithOpenApi();
        group.MapGet("/rewind/{viewId:guid}/snapshot", GetRewindSnapshot).WithName("InsightsRewindSnapshot").WithOpenApi();
        group.MapGet("/workflow/config", GetWorkflowConfig).WithName("InsightsWorkflowConfig").WithOpenApi();
        group.MapGet("/workflow", GetWorkflowReport).WithName("InsightsWorkflow").WithOpenApi();
        group.MapGet("/workflow/cases", GetWorkflowCases).WithName("InsightsWorkflowCases").WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetConfig(
        HttpContext http, IUserService users, IInsightsService insights, ISettingsService settings, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;

        var clock = await insights.GetClockAsync(ct);
        var period = await settings.GetAsync<string>(SettingKeys.Insights.DefaultPeriod, ct);
        if (period is not ("today" or "week" or "month" or "year")) period = "month";
        return Results.Ok(new
        {
            defaultPeriod = period,
            timeZone = clock.TimeZone.Id,
            today = clock.Today,
            maxBuckets = InsightsCalendar.MaxBuckets,
            maxAgents = await GetCompareMaxAsync(settings, ct),
            openedMinSeconds = await GetMinOpenSecondsAsync(settings, ct),
        });
    }

    private static async Task<IResult> GetReport(
        string kind,
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "outcomes")] string? outcomes,
        HttpContext http, IUserService users, IInsightsService insights, IQueueAccessService queueAccess,
        CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (!TryResolveFilter(kind, outcomes, out var filter, out var filterError))
            return Results.BadRequest(new { error = filterError });

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out var granularity, out var error))
            return Results.BadRequest(new { error });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var report = await insights.GetTicketCountsAsync(filter, range, granularity, scope, ct);
        return Results.Ok(ToDto(report));
    }

    private static async Task<IResult> ExportPdf(
        string kind,
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "outcomes")] string? outcomes,
        [FromQuery(Name = "queueIds")] Guid[]? queueIds,
        HttpContext http, IUserService users, IInsightsService insights, IQueueAccessService queueAccess,
        IAuditLogger audit, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (!TryResolveFilter(kind, outcomes, out var filter, out var filterError))
            return Results.BadRequest(new { error = filterError });

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out var granularity, out var error))
            return Results.BadRequest(new { error });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var report = await insights.GetTicketCountsAsync(filter, range, granularity, scope, ct);

        // The selection only ever narrows what the access-scoped report
        // already holds — ids outside it are ignored, never looked up.
        var allQueues = queueIds is null || queueIds.Length == 0;
        if (!allQueues)
        {
            report = FilterQueues(report, queueIds!.ToHashSet());
            if (report.Queues.Count == 0)
                return Results.BadRequest(new { error = "Select at least one queue." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        var pdf = TicketCountPdfGenerator.Generate(new TicketCountPdfData(report, allQueues, DateTime.UtcNow, actor));

        await audit.LogAsync(new AuditEvent(
            EventType: PdfExportedEvent,
            Actor: actor, ActorRole: role, Target: kind,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new
            {
                from = range.From,
                to = range.To,
                granularity = granularity.ToString().ToLowerInvariant(),
                outcomes = filter.Metric == InsightsMetric.Closed ? filter.Outcomes.ToString() : null,
                queues = allQueues ? null : report.Queues.Select(q => q.Id).ToArray(),
            }), ct);

        var fileName = string.Create(CultureInfo.InvariantCulture,
            $"insights-{kind}-{range.From:yyyy-MM-dd}_{range.To:yyyy-MM-dd}.pdf");
        return Results.File(pdf, "application/pdf", fileName);
    }

    private static async Task<IResult> ListTickets(
        string kind,
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "outcomes")] string? outcomes,
        [FromQuery(Name = "queueIds")] Guid[]? queueIds,
        [FromQuery(Name = "listOffset")] int? listOffset,
        [FromQuery(Name = "limit")] int? limit,
        HttpContext http, IUserService users, IInsightsService insights, IQueueAccessService queueAccess,
        CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (!TryResolveFilter(kind, outcomes, out var filter, out var filterError))
            return Results.BadRequest(new { error = filterError });

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out _, out var error))
            return Results.BadRequest(new { error });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var page = await insights.ListTicketsAsync(
            filter, range, scope, queueIds,
            Math.Max(0, listOffset ?? 0),
            Math.Clamp(limit ?? ListPageSize, 1, ListMaxPageSize),
            ct);

        return Results.Ok(new
        {
            total = page.Total,
            items = page.Items.Select(t => new
            {
                id = t.Id,
                number = t.Number,
                subject = t.Subject,
                requester = t.RequesterName,
                company = t.CompanyName,
                statusName = t.StatusName,
                statusColor = t.StatusColor,
                statusCategory = t.StatusCategory,
                momentUtc = t.MomentUtc,
            }),
        });
    }

    // ---- agents overview ---------------------------------------------------

    private static async Task<IResult> GetAgentReport(
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "agentIds")] Guid[]? agentIds,
        HttpContext http, IUserService users, IInsightsService insights, IAgentInsightsService agentInsights,
        IQueueAccessService queueAccess, ISettingsService settings, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out var granularity, out var error))
            return Results.BadRequest(new { error });

        var (agents, agentError) = await ResolveAgentsAsync(agentIds, agentInsights, settings, ct);
        if (agentError is not null) return Results.BadRequest(new { error = agentError });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var minSeconds = await GetMinOpenSecondsAsync(settings, ct);
        var report = await agentInsights.GetActivityAsync(agents, range, granularity, clock, scope, minSeconds, ct);
        return Results.Ok(ToDto(report));
    }

    private static async Task<IResult> ListAgentTickets(
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "agentId")] Guid? agentId,
        [FromQuery(Name = "sort")] string? sort,
        [FromQuery(Name = "listOffset")] int? listOffset,
        [FromQuery(Name = "limit")] int? limit,
        HttpContext http, IUserService users, IInsightsService insights, IAgentInsightsService agentInsights,
        IQueueAccessService queueAccess, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (!TryParseSort(sort, out var order))
            return Results.BadRequest(new { error = "Unknown sort." });

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out _, out var error))
            return Results.BadRequest(new { error });

        if (agentId is not { } id)
            return Results.BadRequest(new { error = "Pick an agent." });
        var agents = await agentInsights.ResolveAgentsAsync(new[] { id }, ct);
        if (agents.Count == 0)
            return Results.BadRequest(new { error = "Unknown agent." });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var page = await agentInsights.ListTicketsAsync(
            id, range, clock, scope, order,
            Math.Max(0, listOffset ?? 0),
            Math.Clamp(limit ?? ListPageSize, 1, ListMaxPageSize),
            ct);

        return Results.Ok(new
        {
            total = page.Total,
            items = page.Items.Select(ToDto),
        });
    }

    private static async Task<IResult> ListOpenedWithoutAction(
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "agentId")] Guid? agentId,
        [FromQuery(Name = "listOffset")] int? listOffset,
        [FromQuery(Name = "limit")] int? limit,
        HttpContext http, IUserService users, IInsightsService insights, IAgentInsightsService agentInsights,
        IQueueAccessService queueAccess, ISettingsService settings, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out _, out var error))
            return Results.BadRequest(new { error });

        if (agentId is not { } id)
            return Results.BadRequest(new { error = "Pick an agent." });
        var agents = await agentInsights.ResolveAgentsAsync(new[] { id }, ct);
        if (agents.Count == 0)
            return Results.BadRequest(new { error = "Unknown agent." });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var page = await agentInsights.ListOpenedWithoutActionAsync(
            id, range, clock, scope, await GetMinOpenSecondsAsync(settings, ct),
            Math.Max(0, listOffset ?? 0),
            Math.Clamp(limit ?? ListPageSize, 1, ListMaxPageSize),
            ct);

        return Results.Ok(new
        {
            total = page.Total,
            items = page.Items.Select(o => new
            {
                sessionId = o.SessionId,
                ticketId = o.TicketId,
                number = o.Number,
                subject = o.Subject,
                company = o.CompanyName,
                statusName = o.StatusName,
                statusColor = o.StatusColor,
                statusCategory = o.StatusCategory,
                openedUtc = o.OpenedUtc,
                closedUtc = o.ClosedUtc,
                closeReason = o.CloseReason,
                durationSeconds = o.DurationSeconds,
            }),
        });
    }

    private static async Task<IResult> ExportAgentPdf(
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "agentIds")] Guid[]? agentIds,
        [FromQuery(Name = "metric")] string? metric,
        [FromQuery(Name = "sort")] string? sort,
        HttpContext http, IUserService users, IInsightsService insights, IAgentInsightsService agentInsights,
        IQueueAccessService queueAccess, ISettingsService settings, IAuditLogger audit, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (!TryParseMetric(metric, out var chartMetric))
            return Results.BadRequest(new { error = "Unknown metric." });
        if (!TryParseSort(sort, out var order))
            return Results.BadRequest(new { error = "Unknown sort." });

        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out var granularity, out var error))
            return Results.BadRequest(new { error });

        var (agents, agentError) = await ResolveAgentsAsync(agentIds, agentInsights, settings, ct);
        if (agentError is not null) return Results.BadRequest(new { error = agentError });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var minSeconds = await GetMinOpenSecondsAsync(settings, ct);
        var report = await agentInsights.GetActivityAsync(agents, range, granularity, clock, scope, minSeconds, ct);
        var lists = new List<AgentPdfTicketList>(agents.Count);
        foreach (var agent in agents)
        {
            var page = await agentInsights.ListTicketsAsync(agent.Id, range, clock, scope, order, 0, PdfTicketCap, ct);
            var opened = await agentInsights.ListOpenedWithoutActionAsync(agent.Id, range, clock, scope, minSeconds, 0, PdfTicketCap, ct);
            lists.Add(new AgentPdfTicketList(agent, page, opened));
        }

        var (actor, role) = ActorContext.Resolve(http);
        var pdf = AgentActivityPdfGenerator.Generate(
            new AgentActivityPdfData(report, chartMetric, lists, DateTime.UtcNow, actor));

        await audit.LogAsync(new AuditEvent(
            EventType: PdfExportedEvent,
            Actor: actor, ActorRole: role, Target: "agents",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new
            {
                from = range.From,
                to = range.To,
                granularity = granularity.ToString().ToLowerInvariant(),
                agents = agents.Select(a => a.Id).ToArray(),
            }), ct);

        var fileName = string.Create(CultureInfo.InvariantCulture,
            $"insights-agents-{range.From:yyyy-MM-dd}_{range.To:yyyy-MM-dd}.pdf");
        return Results.File(pdf, "application/pdf", fileName);
    }

    /// Validates the agent selection: 1..max distinct ids, each an existing
    /// Agent/Admin. Any id that does not resolve fails the whole request —
    /// never a silently shorter comparison.
    private static async Task<(IReadOnlyList<InsightsAgent> Agents, string? Error)> ResolveAgentsAsync(
        Guid[]? agentIds, IAgentInsightsService agentInsights, ISettingsService settings, CancellationToken ct)
    {
        var ids = (agentIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (ids.Length == 0) return (Array.Empty<InsightsAgent>(), "Pick at least one agent.");

        var max = await GetCompareMaxAsync(settings, ct);
        if (ids.Length > max)
            return (Array.Empty<InsightsAgent>(), $"Compare at most {max} agent{(max == 1 ? "" : "s")} at a time.");

        var agents = await agentInsights.ResolveAgentsAsync(ids, ct);
        return agents.Count == ids.Length
            ? (agents, null)
            : (Array.Empty<InsightsAgent>(), "Unknown agent.");
    }

    private static async Task<int> GetCompareMaxAsync(ISettingsService settings, CancellationToken ct)
    {
        int value;
        try { value = await settings.GetAsync<int>(SettingKeys.Insights.AgentCompareMax, ct); }
        catch { value = 3; }
        return Math.Clamp(value, CompareMaxFloor, CompareMaxCeiling);
    }

    private static async Task<int> GetMinOpenSecondsAsync(ISettingsService settings, CancellationToken ct)
    {
        int value;
        try { value = await settings.GetAsync<int>(SettingKeys.Insights.OpenedNoActionMinSeconds, ct); }
        catch { value = 3; }
        return Math.Clamp(value, 0, 3600);
    }

    internal static bool TryParseSort(string? raw, out AgentTicketSort sort)
    {
        switch ((raw ?? "recent").Trim().ToLowerInvariant())
        {
            case "recent": sort = AgentTicketSort.Recent; return true;
            case "period": sort = AgentTicketSort.PeriodTime; return true;
            case "agent": sort = AgentTicketSort.AgentTime; return true;
            case "ticket": sort = AgentTicketSort.TicketTime; return true;
            default: sort = AgentTicketSort.Recent; return false;
        }
    }

    internal static bool TryParseMetric(string? raw, out AgentChartMetric metric)
    {
        switch ((raw ?? "tickets").Trim().ToLowerInvariant())
        {
            case "tickets": metric = AgentChartMetric.Tickets; return true;
            case "time": metric = AgentChartMetric.Time; return true;
            case "calls": metric = AgentChartMetric.Calls; return true;
            default: metric = AgentChartMetric.Tickets; return false;
        }
    }

    private static object ToDto(AgentActivityReport r) => new
    {
        from = r.Range.From,
        to = r.Range.To,
        granularity = r.Granularity.ToString().ToLowerInvariant(),
        timeZone = r.TimeZoneId,
        today = r.Today,
        agents = r.Agents.Select(a => new { id = a.Id, name = a.Name, email = a.Email, role = a.Role, slot = a.Slot }),
        totals = r.Totals.Select(ToDto),
        buckets = r.Buckets.Select(b => new { start = b.Start, from = b.From, to = b.To, values = b.Values.Select(ToDto) }),
    };

    private static object ToDto(AgentActivityTotals t) => new
    {
        tickets = t.Tickets,
        ticketMinutes = t.TicketMinutes,
        calls = t.Calls,
        callsIn = t.CallsIn,
        callsOut = t.CallsOut,
        callSeconds = t.CallSeconds,
        openedNoAction = t.OpenedNoAction,
    };

    private static object ToDto(AgentTicketItem t) => new
    {
        id = t.Id,
        number = t.Number,
        subject = t.Subject,
        requester = t.RequesterName,
        company = t.CompanyName,
        statusName = t.StatusName,
        statusColor = t.StatusColor,
        statusCategory = t.StatusCategory,
        lastActionUtc = t.LastActionUtc,
        lastEntryDate = t.LastEntryDate,
        periodMinutes = t.PeriodMinutes,
        agentMinutes = t.AgentMinutes,
        ticketMinutes = t.TicketMinutes,
    };

    // ---- helpers -----------------------------------------------------------

    public sealed record ReportQuery(
        [FromQuery] string? Period,
        [FromQuery] int? Offset,
        [FromQuery] string? From,
        [FromQuery] string? To,
        [FromQuery] string? Granularity);

    // ---- Rewind (v0.1.31) ----------------------------------------------------

    /// Chart windows the page offers; the window ends at <c>end</c> (or the
    /// latest slot) and reaches back this far.
    private static readonly Dictionary<string, TimeSpan> RewindRanges = new(StringComparer.Ordinal)
    {
        ["4h"] = TimeSpan.FromHours(4),
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
    };

    private static async Task<IResult> ListRewindViews(
        HttpContext http, IUserService users, IRewindService rewind, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        var (_, role) = ActorContext.Resolve(http);
        var views = await rewind.ListViewsAsync(ActorContext.GetUserId(http), role, ct);
        return Results.Ok(new { intervalMinutes = await rewind.GetIntervalAsync(ct), views });
    }

    private static async Task<IResult> GetRewindSeries(
        Guid viewId,
        [FromQuery(Name = "range")] string? range,
        [FromQuery(Name = "end")] DateTimeOffset? end,
        HttpContext http, IUserService users, IRewindService rewind, IQueueAccessService queueAccess,
        CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (await RequireRewindViewAsync(http, rewind, viewId, ct) is { } missing) return missing;
        if (!RewindRanges.TryGetValue(range ?? "24h", out var span))
            return Results.BadRequest(new { error = "range must be 4h, 24h or 7d." });

        // "end" only moves the window back; the service clamps it to the
        // latest server-time slot, so a future (or client-clock) value
        // never reaches past now.
        if (end is { } e && e.UtcDateTime.Year < 2000)
            return Results.BadRequest(new { error = "end is out of range." });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var series = await rewind.GetSeriesAsync(viewId, end?.UtcDateTime, span, scope, ct);
        return Results.Ok(series);
    }

    private static async Task<IResult> GetRewindSnapshot(
        Guid viewId,
        [FromQuery(Name = "at")] DateTimeOffset? at,
        HttpContext http, IUserService users, IRewindService rewind, IQueueAccessService queueAccess,
        CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        if (await RequireRewindViewAsync(http, rewind, viewId, ct) is { } missing) return missing;
        if (at is { } a && a.UtcDateTime.Year < 2000)
            return Results.BadRequest(new { error = "at is out of range." });

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var snapshot = await rewind.GetSnapshotAsync(viewId, at?.UtcDateTime ?? DateTime.UtcNow, scope, ct);
        return Results.Ok(snapshot);
    }

    // ---- Workflow (v0.1.32) ---------------------------------------------------

    private const string WorkflowCasesViewedEvent = "insights.workflow.cases_viewed";

    private static async Task<IResult> GetWorkflowConfig(
        HttpContext http, IUserService users, IRewindService rewind, IWorkflowReportService workflow,
        CancellationToken ct)
    {
        if (await RequireWorkflowAsync(http, users, ct) is { } deny) return deny;
        var (_, role) = ActorContext.Resolve(http);
        var views = await rewind.ListViewsAsync(ActorContext.GetUserId(http), role, ct);
        return Results.Ok(new { views, limits = await workflow.GetLimitsAsync(ct), maxDays = IWorkflowReportService.MaxDays });
    }

    private static async Task<IResult> GetWorkflowReport(
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "viewId")] Guid? viewId,
        HttpContext http, IUserService users, IInsightsService insights, IRewindService rewind,
        IWorkflowReportService workflow, IQueueAccessService queueAccess, CancellationToken ct)
    {
        if (await RequireWorkflowAsync(http, users, ct) is { } deny) return deny;
        var resolved = await ResolveWorkflowAsync(http, query, viewId, insights, rewind, ct);
        if (resolved.Error is { } error) return error;

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var report = await workflow.GetReportAsync(resolved.Range, viewId, scope, ct);
        return Results.Ok(report);
    }

    private static async Task<IResult> GetWorkflowCases(
        [AsParameters] ReportQuery query,
        [FromQuery(Name = "viewId")] Guid? viewId,
        [FromQuery(Name = "agentId")] Guid? agentId,
        [FromQuery(Name = "kpi")] string? kpi,
        HttpContext http, IUserService users, IInsightsService insights, IRewindService rewind,
        IWorkflowReportService workflow, IQueueAccessService queueAccess, IAuditLogger audit, CancellationToken ct)
    {
        if (await RequireWorkflowAsync(http, users, ct) is { } deny) return deny;
        if (agentId is not { } agent || agent == Guid.Empty)
            return Results.BadRequest(new { error = "agentId is required." });
        if (!Enum.TryParse<WorkflowKpi>(kpi, ignoreCase: true, out var which) || !Enum.IsDefined(which))
            return Results.BadRequest(new { error = "Unknown kpi." });
        var resolved = await ResolveWorkflowAsync(http, query, viewId, insights, rewind, ct);
        if (resolved.Error is { } error) return error;

        var scope = await ResolveScopeAsync(http, queueAccess, ct);
        var cases = await workflow.GetCasesAsync(resolved.Range, viewId, scope, agent, which, ct);

        // Per-agent compliance detail is personnel data: every look is audited.
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: WorkflowCasesViewedEvent,
            Actor: actor, ActorRole: role, Target: agent.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { kpi = which.ToString(), from = resolved.Range.From, to = resolved.Range.To, viewId }), ct);

        return Results.Ok(new { items = cases.Take(ListMaxPageSize * 5).ToList(), total = cases.Count });
    }

    /// Insights flag (like every tab) plus manager-level access: Admin, or
    /// the Timesheet manager feature. The report measures individual agents.
    private static async Task<IResult?> RequireWorkflowAsync(HttpContext http, IUserService users, CancellationToken ct)
    {
        if (await RequireFlagAsync(http, users, ct) is { } deny) return deny;
        var (_, role) = ActorContext.Resolve(http);
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) return null;
        var flags = await users.GetTimesheetFlagsAsync(ActorContext.GetUserId(http), ct);
        return flags.Manager ? null : Results.Forbid();
    }

    private static async Task<(InsightsRange Range, IResult? Error)> ResolveWorkflowAsync(
        HttpContext http, ReportQuery query, Guid? viewId, IInsightsService insights, IRewindService rewind,
        CancellationToken ct)
    {
        var clock = await insights.GetClockAsync(ct);
        if (!TryResolve(query, clock.Today, out var range, out _, out var error))
            return (range, Results.BadRequest(new { error }));
        if (range.DayCount > IWorkflowReportService.MaxDays)
            return (range, Results.BadRequest(new { error = $"Pick a period of at most {IWorkflowReportService.MaxDays} days." }));
        if (viewId is { } v)
        {
            var (_, role) = ActorContext.Resolve(http);
            var tracked = await rewind.ListViewsAsync(ActorContext.GetUserId(http), role, ct);
            if (!tracked.Any(t => t.Id == v)) return (range, Results.NotFound());
        }
        return (range, null);
    }

    private static async Task<IResult?> RequireRewindViewAsync(
        HttpContext http, IRewindService rewind, Guid viewId, CancellationToken ct)
    {
        var (_, role) = ActorContext.Resolve(http);
        return await rewind.CanReadViewAsync(ActorContext.GetUserId(http), role, viewId, ct)
            ? null : Results.NotFound();
    }

    private static async Task<IResult?> RequireFlagAsync(HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        return await users.GetInsightsEnabledAsync(userId, ct) ? null : Results.Forbid();
    }

    private static Task<QueueAccessScope> ResolveScopeAsync(
        HttpContext http, IQueueAccessService queueAccess, CancellationToken ct)
    {
        var (_, role) = ActorContext.Resolve(http);
        return queueAccess.GetScopeAsync(ActorContext.GetUserId(http), role, ct);
    }

    /// Route kind + optional outcome list → filter. Outcomes only apply to
    /// closed-tickets; absent = all three; unknown tokens are rejected.
    internal static bool TryResolveFilter(
        string kind, string? outcomes, out InsightsTicketFilter filter, out string? error)
    {
        error = null;
        filter = InsightsTicketFilter.NewTickets;
        if (kind == "new-tickets") return true;

        if (string.IsNullOrWhiteSpace(outcomes))
        {
            filter = new InsightsTicketFilter(InsightsMetric.Closed, ClosedOutcomes.All);
            return true;
        }

        var set = ClosedOutcomes.None;
        foreach (var token in outcomes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "resolved": set |= ClosedOutcomes.Resolved; break;
                case "closed": set |= ClosedOutcomes.Closed; break;
                case "merged": set |= ClosedOutcomes.Merged; break;
                default: error = "Unknown outcome."; return false;
            }
        }
        if (set == ClosedOutcomes.None)
        {
            error = "Select at least one outcome.";
            return false;
        }
        filter = new InsightsTicketFilter(InsightsMetric.Closed, set);
        return true;
    }

    /// Parses + validates the query into a local-date range and grouping.
    /// Presets resolve against the server's local date, never the client's.
    internal static bool TryResolve(
        ReportQuery q, DateOnly today,
        out InsightsRange range, out InsightsGranularity granularity, out string? error)
    {
        range = new InsightsRange(today, today);
        granularity = InsightsGranularity.Day;
        error = null;

        InsightsPeriod period;
        switch ((q.Period ?? "month").Trim().ToLowerInvariant())
        {
            case "today": period = InsightsPeriod.Today; break;
            case "week": period = InsightsPeriod.Week; break;
            case "month": period = InsightsPeriod.Month; break;
            case "year": period = InsightsPeriod.Year; break;
            case "custom": period = InsightsPeriod.Custom; break;
            default: error = "Unknown period."; return false;
        }

        if (period == InsightsPeriod.Custom)
        {
            if (!TryParseDate(q.From, out var from) || !TryParseDate(q.To, out var to))
            {
                error = "A custom period needs 'from' and 'to' as yyyy-MM-dd.";
                return false;
            }
            if (from > to)
            {
                error = "'from' must be on or before 'to'.";
                return false;
            }
            if (from.Year < 2000 || to.Year > today.Year + 1)
            {
                error = "Dates must fall between 2000 and next year.";
                return false;
            }
            range = new InsightsRange(from, to);
        }
        else
        {
            range = InsightsCalendar.ResolvePeriod(period, q.Offset ?? 0, today);
        }

        if (string.IsNullOrWhiteSpace(q.Granularity))
        {
            granularity = InsightsCalendar.DefaultGranularity(range);
        }
        else
        {
            switch (q.Granularity.Trim().ToLowerInvariant())
            {
                case "day": granularity = InsightsGranularity.Day; break;
                case "week": granularity = InsightsGranularity.Week; break;
                case "month": granularity = InsightsGranularity.Month; break;
                case "year": granularity = InsightsGranularity.Year; break;
                default: error = "Unknown granularity."; return false;
            }
        }

        if (InsightsCalendar.CountBuckets(range, granularity) > InsightsCalendar.MaxBuckets)
        {
            error = $"This range has more than {InsightsCalendar.MaxBuckets} {granularity.ToString().ToLowerInvariant()}s — pick a coarser grouping.";
            return false;
        }
        return true;
    }

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static TicketCountReport FilterQueues(TicketCountReport report, IReadOnlySet<Guid> keep)
    {
        var indexes = report.Queues
            .Select((q, i) => (q, i))
            .Where(x => keep.Contains(x.q.Id))
            .Select(x => x.i)
            .ToArray();
        return report with
        {
            Queues = indexes.Select(i => report.Queues[i]).ToList(),
            Buckets = report.Buckets
                .Select(b => b with { Counts = indexes.Select(i => b.Counts[i]).ToArray() })
                .ToList(),
        };
    }

    private static object ToDto(TicketCountReport r) => new
    {
        from = r.Range.From,
        to = r.Range.To,
        granularity = r.Granularity.ToString().ToLowerInvariant(),
        timeZone = r.TimeZoneId,
        today = r.Today,
        queues = r.Queues.Select(q => new { id = q.Id, name = q.Name, isActive = q.IsActive, slot = q.Slot }),
        buckets = r.Buckets.Select(b => new { start = b.Start, from = b.From, to = b.To, counts = b.Counts }),
    };
}
