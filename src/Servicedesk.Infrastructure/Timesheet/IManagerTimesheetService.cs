namespace Servicedesk.Infrastructure.Timesheet;

/// Manager-scoped timesheet operations (Tab 2 + Tab 3). Distinct from
/// <see cref="ITimesheetEntryService"/> because:
///   - reads here span all users (and accept rich filters), while the
///     own-row service is intentionally narrowed to one user per call;
///   - writes here pass `actorUserId` separately from `targetUserId` so
///     audit-events surface who edited whose row.
///
/// Authorization (the `timesheet_manager` flag) is enforced at the
/// endpoint layer — this service trusts its caller.
public interface IManagerTimesheetService
{
    Task<IReadOnlyList<TimesheetEntryRow>> ListAsync(
        ManagerEntryFilter filter,
        CancellationToken ct = default);

    /// Lightweight pick-list of users with `timesheet_enabled=true` OR
    /// `timesheet_manager=true`. Used by the manager UI for the user
    /// filter dropdown and the Tab-3 agent selector.
    Task<IReadOnlyList<TimesheetUser>> ListTimesheetUsersAsync(CancellationToken ct = default);

    Task<TimesheetEntryRow?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ManagerUpdateResult> UpdateAsync(
        Guid id,
        TimesheetEntryInput input,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// Per-day rollup for a single user in a single month. Returns one
    /// <see cref="MonthDayRollup"/> per calendar day of the month so the
    /// grid shows blanks for not-filled-in days too. Work-minutes and
    /// absence-minutes are returned separately because a verlof-dag must
    /// not look like a normal 8u-day.
    Task<MonthRollup> GetMonthAsync(
        Guid userId,
        int year,
        int month,
        CancellationToken ct = default);
}

/// Filter for the Tab-2 grid. All fields optional; the service treats
/// the empty filter as "all rows for the last 7 days" so an unbounded
/// query never accidentally returns the full table.
public sealed record ManagerEntryFilter(
    DateOnly? From,
    DateOnly? To,
    Guid? UserId,
    Guid? TicketId,
    Guid? TaskId,
    string? Search,
    int Limit = 500);

public sealed record TimesheetUser(Guid Id, string Email, bool Enabled, bool Manager);

public abstract record ManagerUpdateResult
{
    public sealed record Updated(TimesheetEntryRow Before, TimesheetEntryRow After) : ManagerUpdateResult;
    public sealed record NotFound : ManagerUpdateResult;
    public sealed record ValidationFailed(IReadOnlyList<TimesheetFieldError> Errors) : ManagerUpdateResult;
}

public sealed record MonthRollup(
    Guid UserId,
    string UserEmail,
    int Year,
    int Month,
    IReadOnlyList<MonthDayRollup> Days);

public sealed record MonthDayRollup(
    DateOnly Date,
    int WorkMinutes,
    int AbsenceMinutes,
    int EntryCount,
    IReadOnlyList<MonthDayBreakdown> Breakdown);

/// Per-task breakdown of a single day, so the UI can hover-reveal "3h
/// Servicedesk + 2h Project" without a second fetch.
public sealed record MonthDayBreakdown(
    Guid TaskId,
    string TaskName,
    bool IsAbsence,
    int Minutes);
