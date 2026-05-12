namespace Servicedesk.Infrastructure.Timesheet;

/// CRUD on the `timesheet_tasks` catalogue. Admin-only at the endpoint
/// layer; the service itself doesn't re-check authorization.
public interface ITimesheetTaskService
{
    /// Returns the catalogue ordered by (sort_order, name). When
    /// `includeArchived` is false, archived rows are filtered out — this
    /// is the default the Tab-1 picker uses. The Settings page passes
    /// true so an admin can un-archive a retired task.
    Task<IReadOnlyList<TimesheetTask>> ListAsync(bool includeArchived, CancellationToken ct = default);

    Task<TimesheetTask?> GetAsync(Guid id, CancellationToken ct = default);

    /// Insert a new catalogue row. Returns NameConflict if another
    /// non-archived task already uses the same case-insensitive name —
    /// matches the partial unique index on `lower(name) WHERE NOT archived`.
    Task<CreateTimesheetTaskResult> CreateAsync(
        string name,
        bool requiresTicket,
        bool isAbsence,
        int sortOrder,
        CancellationToken ct = default);

    /// Edit-in-place. Same NameConflict semantics as create; rename to a
    /// name held by another active task is rejected.
    Task<UpdateTimesheetTaskResult> UpdateAsync(
        Guid id,
        string name,
        bool requiresTicket,
        bool isAbsence,
        bool archived,
        int sortOrder,
        CancellationToken ct = default);
}

public abstract record CreateTimesheetTaskResult
{
    public sealed record Created(TimesheetTask Task) : CreateTimesheetTaskResult;
    public sealed record NameConflict : CreateTimesheetTaskResult;
}

public abstract record UpdateTimesheetTaskResult
{
    public sealed record Updated(TimesheetTask Task) : UpdateTimesheetTaskResult;
    public sealed record NotFound : UpdateTimesheetTaskResult;
    public sealed record NameConflict : UpdateTimesheetTaskResult;
}
