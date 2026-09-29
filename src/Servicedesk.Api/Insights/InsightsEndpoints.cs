using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Settings;

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
public static class InsightsEndpoints
{
    private const string PdfExportedEvent = "insights.report.pdf_exported";
    private const string KindRoute = "/{kind:regex(^(new|closed)-tickets$)}";

    /// Page size of the ticket list under a report (and its hard cap).
    private const int ListPageSize = 50;
    private const int ListMaxPageSize = 200;

    public static IEndpointRouteBuilder MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/insights")
            .WithTags("Insights")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/config", GetConfig).WithName("InsightsConfig").WithOpenApi();
        group.MapGet(KindRoute, GetReport).WithName("InsightsReport").WithOpenApi();
        group.MapGet(KindRoute + "/pdf", ExportPdf).WithName("InsightsReportPdf").WithOpenApi();
        group.MapGet(KindRoute + "/tickets", ListTickets).WithName("InsightsReportTickets").WithOpenApi();

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

    // ---- helpers -----------------------------------------------------------

    public sealed record ReportQuery(
        [FromQuery] string? Period,
        [FromQuery] int? Offset,
        [FromQuery] string? From,
        [FromQuery] string? To,
        [FromQuery] string? Granularity);

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
