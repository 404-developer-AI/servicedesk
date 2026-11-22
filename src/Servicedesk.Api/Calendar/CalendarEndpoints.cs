using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Calendar;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Calendar;

/// v0.1.61 — Outlook calendar planning from forms.
/// - <c>GET /api/settings/calendar</c>: agent projection (is planning on).
/// - <c>GET /api/calendar/availability</c>: free/busy of the picked
///   technicians around a slot — behind "Plan in calendars" (server flag).
/// - <c>GET|POST /api/ticket-forms/{id}/calendar[/retry]</c>: sync status and
///   manual retry of one form's appointments, behind the form's ticket access.
/// - <c>GET /api/admin/users/{id}/calendars</c>: a user's calendars (admin)
///   for the per-user calendar choice.
public static class CalendarEndpoints
{
    public const int MaxWindowDays = 7;

    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/calendar", async (ISettingsService settings, CancellationToken ct) =>
        {
            bool enabled;
            try { enabled = await settings.GetAsync<bool>(SettingKeys.Calendar.Enabled, ct); }
            catch { enabled = false; }
            return Results.Ok(new { enabled });
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetCalendarSettings");

        app.MapGet("/api/calendar/availability", async (
            [FromQuery] string? userIds, [FromQuery] DateTime? start, [FromQuery] DateTime? end, [FromQuery] Guid? excludeFormId,
            ISettingsService settings, ICalendarSyncRepository repo, IGraphCalendarClient graph, CancellationToken ct) =>
        {
            bool enabled;
            try { enabled = await settings.GetAsync<bool>(SettingKeys.Calendar.Enabled, ct); }
            catch { enabled = false; }
            if (!enabled) return Results.Ok(new { enabled = false, users = Array.Empty<object>() });

            if (start is null || end is null) return Results.BadRequest(new { error = "start and end are required." });
            var from = DateTime.SpecifyKind(start.Value.ToUniversalTime(), DateTimeKind.Utc);
            var to = DateTime.SpecifyKind(end.Value.ToUniversalTime(), DateTimeKind.Utc);
            if (to <= from) return Results.BadRequest(new { error = "end must be after start." });
            if ((to - from).TotalDays > MaxWindowDays) return Results.BadRequest(new { error = $"At most {MaxWindowDays} days at once." });

            var ids = (userIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(20).ToList();
            if (ids.Count == 0) return Results.Ok(new { enabled = true, users = Array.Empty<object>() });
            var users = (await repo.GetUsersAsync(ids, ct)).Values.Where(u => u.IsActive).ToList();

            // The form's own appointment is not a clash with itself.
            DateTime? ownStart = null, ownEnd = null;
            if (excludeFormId is { } fid && await repo.GetFormContextAsync(fid, ct) is { } fctx
                && FormOutlookMapping.Parse(fctx.OutlookJson) is { Enabled: true } map)
            {
                var appt = CalendarSyncService.Read(map, FormDefinition.Parse(fctx.SnapshotJson),
                    global::System.Text.Json.Nodes.JsonNode.Parse(fctx.AnswersJson) as global::System.Text.Json.Nodes.JsonObject ?? new(), fctx);
                ownStart = appt?.StartUtc;
                ownEnd = appt?.EndUtc;
            }

            try
            {
                var schedule = await graph.GetScheduleAsync(users.Select(u => u.Email).ToList(), from, to, ct);
                return Results.Ok(new
                {
                    enabled = true,
                    users = users.Select(u => new
                    {
                        userId = u.Id,
                        name = u.Name,
                        reachable = schedule.ContainsKey(u.Email),
                        items = (schedule.TryGetValue(u.Email, out var items) ? items : Array.Empty<CalendarBusyItem>())
                            .Where(i => i.EndUtc > from && i.StartUtc < to)
                            .Where(i => !(ownStart == i.StartUtc && ownEnd == i.EndUtc))
                            .OrderBy(i => i.StartUtc)
                            .Select(i => new
                            {
                                startUtc = i.StartUtc, endUtc = i.EndUtc, status = i.Status,
                                subject = i.Subject, location = i.Location, isPrivate = i.IsPrivate,
                            }),
                    }),
                });
            }
            catch (CalendarSyncException ex)
            {
                // Availability is advice: say why it is unavailable, never 500.
                return Results.Ok(new { enabled = true, error = ex.Message, users = Array.Empty<object>() });
            }
        })
        .WithTags("Calendar")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .RequireFeature(UserFeature.CalendarPlanning)
        .WithName("GetCalendarAvailability");

        var form = app.MapGroup("/api/ticket-forms/{formId:guid}/calendar")
            .WithTags("Calendar")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        form.MapGet("/", async (Guid formId, HttpContext http, ITicketFormService forms, ICalendarSyncService calendar, CancellationToken ct) =>
        {
            // Ticket access first (404 without it), like every form route.
            try { await forms.GetAsync(TicketEndpoints.ResolveMutationActor(http), formId, ct); }
            catch (FormRejectedException) { return Results.NotFound(); }
            var rows = await calendar.StatusAsync(formId, ct);
            return Results.Ok(new { items = rows.Select(MapRow) });
        }).WithName("GetFormCalendarStatus");

        form.MapPost("/retry", async (Guid formId, HttpContext http, ITicketFormService forms, ICalendarSyncService calendar, CancellationToken ct) =>
        {
            try { await forms.GetAsync(TicketEndpoints.ResolveMutationActor(http), formId, ct); }
            catch (FormRejectedException) { return Results.NotFound(); }
            var n = await calendar.RetryAsync(formId, ct);
            return Results.Ok(new { retried = n });
        }).WithName("RetryFormCalendar");

        app.MapGet("/api/admin/users/{id:guid}/calendars", async (
            Guid id, ICalendarSyncRepository repo, IGraphCalendarClient graph, CancellationToken ct) =>
        {
            var user = (await repo.GetUsersAsync(new[] { id }, ct)).Values.FirstOrDefault();
            if (user is null) return Results.NotFound();
            try
            {
                var list = await graph.ListCalendarsAsync(user.Email, ct);
                return Results.Ok(new { items = list.Select(c => new { id = c.Id, name = c.Name, isDefault = c.IsDefault, canEdit = c.CanEdit }) });
            }
            catch (CalendarSyncException ex)
            {
                return Results.Ok(new { items = Array.Empty<object>(), error = ex.Message });
            }
        })
        .WithTags("AdminUsers")
        .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
        .WithName("ListUserCalendars");

        return app;
    }

    private static object MapRow(CalendarEventRow r) => new
    {
        id = r.Id,
        userId = r.UserId,
        userName = r.UserName,
        isTeams = r.IsTeams,
        status = r.Status,
        webLink = r.WebLink,
        joinUrl = r.JoinUrl,
        attempts = r.Attempts,
        nextAttemptUtc = r.NextAttemptUtc,
        lastError = r.LastError,
        updatedUtc = r.UpdatedUtc,
    };
}
