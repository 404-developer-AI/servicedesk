using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// Agent-facing endpoints for the Tab-1 "eigen registratie" page. Every
/// route here scopes by `userId = ActorContext.GetUserId(...)` — no path
/// or body parameter can override which user's rows are touched. The
/// service mirrors that scope in its WHERE clauses, so even a buggy
/// endpoint can't leak another user's entries.
///
/// The feature-flag gate (timesheet_enabled) is enforced at the
/// frontend-routing layer + by hiding the nav item; the backend allows
/// any authenticated agent/admin to call these endpoints because:
///   (a) without a UI surface, an admin curl request is a deliberate
///       choice and still operates on the admin's own rows;
///   (b) the flag controls feature visibility, not authorization in
///       the security sense.
public static class TimesheetEntryEndpoints
{
    public static IEndpointRouteBuilder MapTimesheetEntryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/entries")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (
            string? date,
            HttpContext http,
            ITimesheetEntryService svc,
            CancellationToken ct) =>
        {
            if (!TryParseDate(date, out var entryDate))
                return Results.BadRequest(new { error = "Query parameter 'date' is required as YYYY-MM-DD." });

            var userId = ActorContext.GetUserId(http);
            var rows = await svc.ListByDateAsync(userId, entryDate, ct);
            return Results.Ok(new { date = entryDate.ToString("yyyy-MM-dd"), items = rows });
        }).WithName("ListTimesheetEntries").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] EntryRequest req,
            HttpContext http,
            ITimesheetEntryService svc,
            IAuditLogger audit,
            ITimesheetEntryNotifier notifier,
            CancellationToken ct) =>
        {
            if (!TryBuildInput(req, out var input, out var error)) return error!;
            var userId = ActorContext.GetUserId(http);

            var result = await svc.CreateAsync(userId, input, ct);
            return result switch
            {
                CreateTimesheetEntryResult.Created created => await AuditNotifyAndReturn(audit, http, notifier,
                    TimesheetAudit.EntryCreated, created.Row, Results.Ok(created.Row), ct),
                CreateTimesheetEntryResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled create-entry result."),
            };
        }).WithName("CreateTimesheetEntry").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] EntryRequest req,
            HttpContext http,
            ITimesheetEntryService svc,
            IAuditLogger audit,
            ITimesheetEntryNotifier notifier,
            CancellationToken ct) =>
        {
            if (!TryBuildInput(req, out var input, out var error)) return error!;
            var userId = ActorContext.GetUserId(http);

            // Capture the pre-edit ticket id so a re-link from ticket A to
            // ticket B (or to no-ticket) still pings ticket A's viewers.
            var before = await svc.GetForUserAsync(id, userId, ct);

            var result = await svc.UpdateAsync(id, userId, input, ct);
            return result switch
            {
                UpdateTimesheetEntryResult.Updated updated => await AuditNotifyAndReturn(audit, http, notifier,
                    TimesheetAudit.EntryUpdated, updated.Row, Results.Ok(updated.Row), ct,
                    extraTicketId: before?.TicketId != updated.Row.TicketId ? before?.TicketId : null),
                UpdateTimesheetEntryResult.NotFound => Results.NotFound(),
                UpdateTimesheetEntryResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled update-entry result."),
            };
        }).WithName("UpdateTimesheetEntry").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext http,
            ITimesheetEntryService svc,
            IAuditLogger audit,
            ITimesheetEntryNotifier notifier,
            CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var before = await svc.GetForUserAsync(id, userId, ct);

            var deleted = await svc.DeleteOwnAsync(id, userId, ct);
            if (!deleted) return Results.NotFound();

            await TimesheetAudit.WriteAsync(audit, http,
                TimesheetAudit.EntryDeleted, id.ToString(),
                new { user_id = userId });
            await notifier.NotifyEntryChangedAsync(before?.TicketId, ct);
            return Results.NoContent();
        }).WithName("DeleteTimesheetEntry").WithOpenApi();

        // v0.0.35-E — caller's own effective preferences. Used by Tab 1
        // to seed the start-time of a new day. Reads override-or-global
        // per field so the UI never has to merge.
        app.MapGet("/api/timesheet/me/preferences", async (
            HttpContext http,
            ITimesheetPreferencesService prefs,
            CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var p = await prefs.GetEffectiveAsync(userId, ct);
            return Results.Ok(p);
        })
        .WithTags("Timesheet")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetTimesheetSelfPreferences")
        .WithOpenApi();

        // v0.0.74 — self-service write of the caller's own default Tab-1 task.
        // Scoped to the caller's own row (userId from the session, never the
        // body), so this is safe for any agent. `taskId: null` clears it.
        app.MapPut("/api/timesheet/me/preferences/default-task", async (
            [FromBody] DefaultTaskRequest req,
            HttpContext http,
            ITimesheetPreferencesService prefs,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var result = await prefs.UpdateDefaultTaskAsync(userId, req.TaskId, ct);
            switch (result)
            {
                case UpdateDefaultTaskResult.Updated u:
                    await TimesheetAudit.WriteAsync(audit, http, TimesheetAudit.SelfDefaultTaskChanged,
                        userId.ToString(), new { default_task_id = u.TaskId });
                    return Results.Ok(new { defaultTaskId = u.TaskId });
                case UpdateDefaultTaskResult.TaskNotFound:
                    return Results.UnprocessableEntity(
                        new { error = "That task does not exist or is archived." });
                case UpdateDefaultTaskResult.UserNotFound:
                    return Results.NotFound();
                default:
                    return Results.Problem("Unhandled set-default-task result.");
            }
        })
        .WithTags("Timesheet")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("SetTimesheetSelfDefaultTask")
        .WithOpenApi();

        return app;
    }

    private static async Task<IResult> AuditNotifyAndReturn(
        IAuditLogger audit,
        HttpContext http,
        ITimesheetEntryNotifier notifier,
        string eventType,
        TimesheetEntryRow row,
        IResult body,
        CancellationToken ct,
        Guid? extraTicketId = null)
    {
        await TimesheetAudit.WriteAsync(audit, http, eventType, row.Id.ToString(),
            new
            {
                user_id = row.UserId,
                entry_date = row.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                start_minutes = row.StartMinutes,
                end_minutes = row.EndMinutes,
                minutes = row.Minutes,
                task_id = row.TaskId,
                task_name = row.TaskName,
                ticket_id = row.TicketId,
                ticket_number = row.TicketNumber,
            });
        await notifier.NotifyEntryChangedAsync(row.TicketId, ct);
        // On a re-link the old ticket's viewers also need to refetch — its
        // panel should drop the row that just moved away.
        if (extraTicketId is { } extra && extra != Guid.Empty && extra != row.TicketId)
            await notifier.NotifyEntryChangedAsync(extra, ct);
        return body;
    }

    private static bool TryBuildInput(EntryRequest req, out TimesheetEntryInput input, out IResult? error)
    {
        input = default!;
        if (!TryParseDate(req.EntryDate, out var entryDate))
        {
            error = Results.BadRequest(new { error = "entryDate is required as YYYY-MM-DD." });
            return false;
        }
        if (req.StartMinutes is null || req.EndMinutes is null)
        {
            error = Results.BadRequest(new { error = "startMinutes and endMinutes are required." });
            return false;
        }
        if (req.TaskId is null || req.TaskId == Guid.Empty)
        {
            error = Results.BadRequest(new { error = "taskId is required." });
            return false;
        }

        input = new TimesheetEntryInput(
            EntryDate: entryDate,
            StartMinutes: req.StartMinutes.Value,
            EndMinutes: req.EndMinutes.Value,
            TaskId: req.TaskId.Value,
            TicketId: req.TicketId,
            Description: req.Description ?? "");
        error = null;
        return true;
    }

    private static bool TryParseDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateOnly.TryParseExact(
            raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public sealed record EntryRequest(
        string? EntryDate,
        int? StartMinutes,
        int? EndMinutes,
        Guid? TaskId,
        Guid? TicketId,
        string? Description);

    /// Body for the self-service default-task write. `null` clears the
    /// preference (UI falls back to the first active task).
    public sealed record DefaultTaskRequest(Guid? TaskId);
}
