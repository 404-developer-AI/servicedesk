using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// Manager-scoped Timesheet endpoints (Tab 2 + Tab 3 backends).
///
/// Authorization is two-layered:
///   1. ASP.NET <c>RequireAgent</c> ensures the call carries a valid
///      session for an Agent or Admin;
///   2. <see cref="EnsureManagerAsync"/> further requires the per-user
///      <c>timesheet_manager</c> flag. Admins are NOT auto-granted —
///      the flag is intentionally orthogonal to the role so an admin can
///      configure the system without seeing time-registration data.
public static class TimesheetManagerEndpoints
{
    public static IEndpointRouteBuilder MapTimesheetManagerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/manager")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // -- users (filter dropdown) -------------------------------------
        group.MapGet("/users", async (
            HttpContext http,
            IUserService users,
            IManagerTimesheetService svc,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();
            var list = await svc.ListTimesheetUsersAsync(ct);
            return Results.Ok(list);
        }).WithName("ListTimesheetUsers").WithOpenApi();

        // -- entries list (Tab 2) ----------------------------------------
        group.MapGet("/entries", async (
            string? from, string? to, Guid? userId, Guid? ticketId, Guid? taskId, string? search, int? limit,
            HttpContext http,
            IUserService users,
            IManagerTimesheetService svc,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();

            DateOnly? fromDate = TryParseDateOpt(from, out var f) ? f : null;
            DateOnly? toDate = TryParseDateOpt(to, out var t) ? t : null;

            var filter = new ManagerEntryFilter(
                From: fromDate, To: toDate,
                UserId: userId, TicketId: ticketId, TaskId: taskId,
                Search: search,
                Limit: limit ?? 500);

            var rows = await svc.ListAsync(filter, ct);
            return Results.Ok(new { items = rows });
        }).WithName("ListManagerTimesheetEntries").WithOpenApi();

        // -- update (Tab 2 inline edit) ----------------------------------
        group.MapPut("/entries/{id:guid}", async (
            Guid id,
            [FromBody] TimesheetEntryEndpoints.EntryRequest req,
            HttpContext http,
            IUserService users,
            IManagerTimesheetService svc,
            IAuditLogger audit,
            ITimesheetEntryNotifier notifier,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();
            if (!TryBuildInput(req, out var input, out var error)) return error!;

            var result = await svc.UpdateAsync(id, input, ct);
            return result switch
            {
                ManagerUpdateResult.Updated upd => await AuditUpdateAndReturn(audit, http, notifier, upd, ct),
                ManagerUpdateResult.NotFound => Results.NotFound(),
                ManagerUpdateResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled manager-update result."),
            };
        }).WithName("ManagerUpdateTimesheetEntry").WithOpenApi();

        // -- delete (Tab 2) ----------------------------------------------
        group.MapDelete("/entries/{id:guid}", async (
            Guid id,
            HttpContext http,
            IUserService users,
            IManagerTimesheetService svc,
            IAuditLogger audit,
            ITimesheetEntryNotifier notifier,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();

            var before = await svc.GetAsync(id, ct);
            if (before is null) return Results.NotFound();

            var deleted = await svc.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            await TimesheetAudit.WriteAsync(audit, http,
                TimesheetAudit.EntryDeletedByManager, id.ToString(),
                new
                {
                    target_user_id = before.UserId,
                    target_user_email = before.UserEmail,
                    entry_date = before.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    start_minutes = before.StartMinutes,
                    end_minutes = before.EndMinutes,
                    minutes = before.Minutes,
                    task_id = before.TaskId,
                    task_name = before.TaskName,
                    ticket_id = before.TicketId,
                    ticket_number = before.TicketNumber,
                });
            await notifier.NotifyEntryChangedAsync(before.TicketId, ct);
            return Results.NoContent();
        }).WithName("ManagerDeleteTimesheetEntry").WithOpenApi();

        // -- month rollup (Tab 3) ----------------------------------------
        group.MapGet("/month", async (
            Guid? userId, int? year, int? month,
            HttpContext http,
            IUserService users,
            IManagerTimesheetService svc,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();
            if (userId is null || userId == Guid.Empty)
                return Results.BadRequest(new { error = "userId is required." });
            if (year is null || month is null || month is < 1 or > 12)
                return Results.BadRequest(new { error = "year and month (1-12) are required." });

            var rollup = await svc.GetMonthAsync(userId.Value, year.Value, month.Value, ct);
            return Results.Ok(rollup);
        }).WithName("GetTimesheetMonth").WithOpenApi();

        // -- CSV export (Tab 3) ------------------------------------------
        group.MapGet("/month/export.csv", async (
            Guid? userId, int? year, int? month,
            HttpContext http,
            IUserService users,
            IManagerTimesheetService svc,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();
            if (userId is null || userId == Guid.Empty)
                return Results.BadRequest(new { error = "userId is required." });
            if (year is null || month is null || month is < 1 or > 12)
                return Results.BadRequest(new { error = "year and month (1-12) are required." });

            var rollup = await svc.GetMonthAsync(userId.Value, year.Value, month.Value, ct);
            var csv = BuildMonthCsv(rollup);
            var fileName = $"timesheet_{Sanitize(rollup.UserEmail)}_{year:D4}-{month:D2}.csv";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(),
                "text/csv; charset=utf-8", fileName);
        }).WithName("ExportTimesheetMonthCsv").WithOpenApi();

        // -- effective preferences for any user (Tab 3 colour grid) ------
        group.MapGet("/preferences/{userId:guid}", async (
            Guid userId,
            HttpContext http,
            IUserService users,
            ITimesheetPreferencesService prefs,
            CancellationToken ct) =>
        {
            if (!await EnsureManagerAsync(http, users, ct)) return Results.Forbid();
            var p = await prefs.GetEffectiveAsync(userId, ct);
            return Results.Ok(p);
        }).WithName("GetTimesheetUserPreferences").WithOpenApi();

        return app;
    }

    private static async Task<bool> EnsureManagerAsync(HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        var flags = await users.GetTimesheetFlagsAsync(userId, ct);
        return flags.Manager;
    }

    private static async Task<IResult> AuditUpdateAndReturn(
        IAuditLogger audit,
        HttpContext http,
        ITimesheetEntryNotifier notifier,
        ManagerUpdateResult.Updated upd,
        CancellationToken ct)
    {
        var before = upd.Before;
        var after = upd.After;
        await TimesheetAudit.WriteAsync(audit, http,
            TimesheetAudit.EntryEditedByManager, after.Id.ToString(),
            new
            {
                target_user_id = after.UserId,
                target_user_email = after.UserEmail,
                before = new
                {
                    entry_date = before.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    start_minutes = before.StartMinutes,
                    end_minutes = before.EndMinutes,
                    minutes = before.Minutes,
                    task_id = before.TaskId,
                    task_name = before.TaskName,
                    ticket_id = before.TicketId,
                    ticket_number = before.TicketNumber,
                    description = before.Description,
                },
                after = new
                {
                    entry_date = after.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    start_minutes = after.StartMinutes,
                    end_minutes = after.EndMinutes,
                    minutes = after.Minutes,
                    task_id = after.TaskId,
                    task_name = after.TaskName,
                    ticket_id = after.TicketId,
                    ticket_number = after.TicketNumber,
                    description = after.Description,
                },
            });
        await notifier.NotifyEntryChangedAsync(after.TicketId, ct);
        // On a re-link the old ticket's panel must also refetch.
        if (before.TicketId is { } oldId && oldId != after.TicketId)
            await notifier.NotifyEntryChangedAsync(oldId, ct);
        return Results.Ok(after);
    }

    private static bool TryBuildInput(
        TimesheetEntryEndpoints.EntryRequest req,
        out TimesheetEntryInput input,
        out IResult? error)
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

    private static bool TryParseDateOpt(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateOnly.TryParseExact(
            raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// Trims the email to a filename-safe slug. Keeps it short and
    /// predictable for ops who batch-process exports. No round-trip
    /// safety needed.
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
            else if (ch == '@' || ch == '.') sb.Append('_');
        }
        return sb.ToString();
    }

    private static string BuildMonthCsv(MonthRollup r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,weekday,work_minutes,work_hours,absence_minutes,absence_hours,entry_count,breakdown");
        var inv = CultureInfo.InvariantCulture;
        foreach (var d in r.Days)
        {
            var date = d.Date.ToString("yyyy-MM-dd", inv);
            var weekday = d.Date.DayOfWeek.ToString();
            var breakdown = string.Join("; ",
                d.Breakdown.Select(b => $"{b.TaskName}={(b.Minutes / 60.0).ToString("0.##", inv)}h"));
            sb.Append(date).Append(',')
              .Append(weekday).Append(',')
              .Append(d.WorkMinutes).Append(',')
              .Append((d.WorkMinutes / 60.0).ToString("0.##", inv)).Append(',')
              .Append(d.AbsenceMinutes).Append(',')
              .Append((d.AbsenceMinutes / 60.0).ToString("0.##", inv)).Append(',')
              .Append(d.EntryCount).Append(',')
              .Append(CsvQuote(breakdown))
              .AppendLine();
        }
        // Footer with month totals so payroll spreadsheets show a sum
        // even without manually summing the column.
        var totalWork = r.Days.Sum(x => x.WorkMinutes);
        var totalAbs = r.Days.Sum(x => x.AbsenceMinutes);
        sb.Append("TOTAL,,")
          .Append(totalWork).Append(',')
          .Append((totalWork / 60.0).ToString("0.##", inv)).Append(',')
          .Append(totalAbs).Append(',')
          .Append((totalAbs / 60.0).ToString("0.##", inv)).Append(",,")
          .AppendLine();
        return sb.ToString();
    }

    private static string CsvQuote(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
