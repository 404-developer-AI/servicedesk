namespace Servicedesk.Infrastructure.Timesheet;

/// CRUD for the calling agent's own timesheet entries (Tab 1).
///
/// Manager-scoped reads/writes (Tab 2, Tab 3, audit-on-mutation by other
/// users) land in a separate service in commit C/D and are not part of
/// this interface — keeping the surface narrow makes the Tab-1
/// authorization story trivial: every call here is "own row only".
public interface ITimesheetEntryService
{
    /// All entries this user has on the given date, joined with task +
    /// ticket + company for the grid. Ordered by start_minutes ASC so the
    /// list reads top-to-bottom by start time.
    Task<IReadOnlyList<TimesheetEntryRow>> ListByDateAsync(
        Guid userId,
        DateOnly date,
        CancellationToken ct = default);

    Task<TimesheetEntryRow?> GetForUserAsync(
        Guid id,
        Guid userId,
        CancellationToken ct = default);

    Task<CreateTimesheetEntryResult> CreateAsync(
        Guid userId,
        TimesheetEntryInput input,
        CancellationToken ct = default);

    Task<UpdateTimesheetEntryResult> UpdateAsync(
        Guid id,
        Guid userId,
        TimesheetEntryInput input,
        CancellationToken ct = default);

    /// Own-rows only. Returns false if the row doesn't exist or belongs
    /// to another user — endpoint translates both to 404 to avoid
    /// disclosing whether the id exists.
    Task<bool> DeleteOwnAsync(Guid id, Guid userId, CancellationToken ct = default);
}

public sealed record TimesheetEntryInput(
    DateOnly EntryDate,
    int StartMinutes,
    int EndMinutes,
    Guid TaskId,
    Guid? TicketId,
    string Description);

public abstract record CreateTimesheetEntryResult
{
    public sealed record Created(TimesheetEntryRow Row) : CreateTimesheetEntryResult;
    public sealed record ValidationFailed(IReadOnlyList<TimesheetFieldError> Errors) : CreateTimesheetEntryResult;
}

public abstract record UpdateTimesheetEntryResult
{
    public sealed record Updated(TimesheetEntryRow Row) : UpdateTimesheetEntryResult;
    public sealed record NotFound : UpdateTimesheetEntryResult;
    public sealed record ValidationFailed(IReadOnlyList<TimesheetFieldError> Errors) : UpdateTimesheetEntryResult;
}

/// Field-level validation error so the UI can highlight exactly which
/// input failed and why. `field` matches the JSON property the client
/// sent ("startMinutes", "ticketId", …).
public sealed record TimesheetFieldError(string Field, string Message);
