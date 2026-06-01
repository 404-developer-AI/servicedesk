namespace Servicedesk.Infrastructure.Timesheet;

/// Backend for the secret-gated migration import surface (v0.0.54).
///
/// The standalone migration tool calls three things: list the target
/// task catalogue and the target users (so the operator can build a
/// source→target mapping file), then push normalised rows in batches.
///
/// Import deliberately bypasses the app-level entry validation
/// (office-hours / overlap / required-ticket) that the interactive
/// Tab-1 endpoint enforces — historical data predates those rules and
/// must land verbatim. The database CHECK on the time-window still
/// applies, so rows that violate it (end &lt;= start, out of 0..1440)
/// are reported as skipped rather than silently dropped or force-fed.
public interface ITimesheetImportService
{
    /// Target task catalogue, including archived tasks, so the operator
    /// can map every source task even onto a since-archived target.
    Task<IReadOnlyList<TimesheetTask>> ListTasksAsync(CancellationToken ct = default);

    /// Agent/Admin users eligible to own timesheet rows, for mapping the
    /// source employees onto. Includes inactive accounts so an offboarded
    /// employee's historical rows can still be attributed.
    Task<IReadOnlyList<TimesheetImportUser>> ListUsersAsync(CancellationToken ct = default);

    /// Upsert a batch of normalised rows, keyed on (importSource,
    /// ImportRef) for idempotent re-runs. Ticket links are resolved
    /// server-side from the Zammad ticket-number; an unmatched number
    /// leaves the row's ticket empty (per migration decision).
    Task<TimesheetImportBatchResult> ImportBatchAsync(
        string importSource,
        IReadOnlyList<TimesheetImportRow> rows,
        CancellationToken ct = default);
}

/// One mappable target user.
public sealed record TimesheetImportUser(
    Guid Id,
    string Email,
    string Role,
    bool IsActive,
    bool TimesheetEnabled);

/// One normalised source row as sent by the migration tool. Time is
/// already converted to the local-day model (entry date + minutes
/// since midnight). Ticket is sent as the raw Zammad number; the
/// server resolves it to a ticket id (or null).
public sealed record TimesheetImportRow(
    string ImportRef,
    Guid UserId,
    Guid TaskId,
    string? ZammadTicketNumber,
    DateOnly EntryDate,
    int StartMinutes,
    int EndMinutes,
    bool Invoiced,
    string Description,
    Guid? CreatedByUserId,
    Guid? UpdatedByUserId,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? UpdatedUtc);

/// Per-batch outcome. `Imported` counts rows that were inserted or
/// updated (idempotent upsert — the two are not distinguished because
/// for a migration only "landed" matters). `Skipped` carries a capped
/// list of (ref, reason) so the operator sees what didn't make it
/// without flooding the response on a 53K-row run.
public sealed record TimesheetImportBatchResult(
    int Received,
    int Imported,
    int WithoutTicketMatch,
    IReadOnlyList<TimesheetImportSkip> Skipped);

public sealed record TimesheetImportSkip(string ImportRef, string Reason);
