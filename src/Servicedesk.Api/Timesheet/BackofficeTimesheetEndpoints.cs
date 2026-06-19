using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// Agent-facing endpoints for the back-office Timesheet → Resolved + CWI
/// tabs (v0.0.56). Both tabs list tickets that entered an admin-selected set
/// of statuses within a chosen month; Resolved additionally drops tickets
/// that already have an Adsolut sales receipt. Per tab a Back-Office reviewer
/// can tick/untick tickets ("BO checked"). The tabs are gated client-side by
/// the per-user `timesheet_backoffice_enabled` flag; the routes here carry
/// RequireAgent — the flag controls feature visibility, the data is
/// install-wide ticket data (not per-user rows).
public static class BackofficeTimesheetEndpoints
{
    public static IEndpointRouteBuilder MapBackofficeTimesheetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/backoffice")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/resolved", GetResolved).WithName("ListBackofficeResolved").WithOpenApi();
        group.MapGet("/cwi", GetCwi).WithName("ListBackofficeCwi").WithOpenApi();
        group.MapGet("/tickets/{ticketId:guid}/hours", GetTicketHours).WithName("GetBackofficeTicketHours").WithOpenApi();
        group.MapPost("/checks", SetChecks).WithName("SetBackofficeChecks").WithOpenApi();

        return group;
    }

    private static async Task<IResult> GetResolved(
        int? year,
        int? month,
        HttpContext http,
        IBackofficeTimesheetService svc,
        ISettingsService settings,
        CancellationToken ct)
        => await ListAsync("resolved", excludeWithReceipt: true,
            SettingKeys.Timesheet.ResolvedTabStatusIds, year, month, http, svc, settings, ct);

    private static async Task<IResult> GetCwi(
        int? year,
        int? month,
        HttpContext http,
        IBackofficeTimesheetService svc,
        ISettingsService settings,
        CancellationToken ct)
        => await ListAsync("cwi", excludeWithReceipt: false,
            SettingKeys.Timesheet.CwiTabStatusIds, year, month, http, svc, settings, ct);

    private static async Task<IResult> ListAsync(
        string context,
        bool excludeWithReceipt,
        string statusSettingKey,
        int? year,
        int? month,
        HttpContext http,
        IBackofficeTimesheetService svc,
        ISettingsService settings,
        CancellationToken ct)
    {
        if (year is not int y || month is not int m || m < 1 || m > 12 || y < 2000 || y > 2100)
            return Results.BadRequest(new { error = "Query parameters 'year' (2000-2100) and 'month' (1-12) are required." });

        var csv = await settings.GetAsync<string>(statusSettingKey, ct);
        var statusIds = ParseStatusIds(csv);
        var (fromUtc, toUtc) = MonthRangeUtc(y, m);
        var viewerId = ActorContext.GetUserId(http);

        var rows = await svc.ListAsync(statusIds, context, excludeWithReceipt, fromUtc, toUtc, viewerId, ct);
        return Results.Ok(new
        {
            year = y,
            month = m,
            statusesConfigured = statusIds.Length > 0,
            items = rows.Select(r => new
            {
                ticketId = r.TicketId,
                ticketNumber = r.TicketNumber,
                subject = r.Subject,
                companyName = r.CompanyName,
                totalMinutes = r.TotalMinutes,
                boChecked = r.BoChecked,
                checkedUtc = r.CheckedUtc,
                checkedByEmail = r.CheckedByEmail,
                enteredUtc = r.EnteredUtc,
                commentThreadId = r.CommentThreadId,
                hasComments = r.HasComments,
                commentsUnread = r.CommentsUnread,
            }),
        });
    }

    private static async Task<IResult> GetTicketHours(
        Guid ticketId,
        IBackofficeTimesheetService svc,
        CancellationToken ct)
    {
        var rows = await svc.GetTicketHoursAsync(ticketId, ct);
        return Results.Ok(new
        {
            totalMinutes = rows.Sum(r => r.Minutes),
            byTask = rows.Select(r => new
            {
                taskName = r.TaskName,
                isAbsence = r.IsAbsence,
                minutes = r.Minutes,
            }),
        });
    }

    public sealed record SetChecksRequest(string Context, IReadOnlyList<Guid> Ids, bool Checked);

    private static readonly string[] ValidContexts = { "resolved", "cwi", "adsolut" };

    private static async Task<IResult> SetChecks(
        SetChecksRequest req,
        HttpContext http,
        IBackofficeTimesheetService svc,
        CancellationToken ct)
    {
        if (req is null || !ValidContexts.Contains(req.Context))
            return Results.BadRequest(new { error = "Field 'context' must be 'resolved', 'cwi' or 'adsolut'." });
        if (req.Ids is null || req.Ids.Count == 0)
            return Results.BadRequest(new { error = "Field 'ids' must contain at least one id." });

        var userId = ActorContext.GetUserId(http);
        await svc.SetChecksAsync(req.Ids, req.Context, req.Checked, userId, ct);
        return Results.Ok(new { ok = true, count = req.Ids.Count, @checked = req.Checked });
    }

    // ---- helpers ---------------------------------------------------------

    private static Guid[] ParseStatusIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<Guid>();
        var set = new HashSet<Guid>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var g)) set.Add(g);
        }
        return set.ToArray();
    }

    /// Calendar month boundaries in the server's local timezone, returned as
    /// UTC instants. The install's clock defines the month (server-based
    /// timing), so a ticket resolved just after local midnight lands in the
    /// correct month regardless of the UTC offset.
    private static (DateTime FromUtc, DateTime ToUtc) MonthRangeUtc(int year, int month)
    {
        var localStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddMonths(1);
        var tz = TimeZoneInfo.Local;
        return (
            TimeZoneInfo.ConvertTimeToUtc(localStart, tz),
            TimeZoneInfo.ConvertTimeToUtc(localEnd, tz));
    }
}
