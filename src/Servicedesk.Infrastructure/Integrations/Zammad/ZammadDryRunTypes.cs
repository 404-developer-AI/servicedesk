namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Lifecycle status of a dry-run / import row.
/// <list type="bullet">
/// <item><c>Pending</c> — written by the API endpoint, waiting for the
/// background worker to pick it up.</item>
/// <item><c>Running</c> — worker is currently processing tickets.</item>
/// <item><c>Completed</c> — every ticket walked, regardless of per-row
/// outcome.</item>
/// <item><c>Failed</c> — fatal error (Zammad outage, mapping table
/// disappeared mid-run). The error message lives on the run row.</item>
/// <item><c>Cancelled</c> — admin clicked Cancel; the worker stopped at
/// the current ticket and remaining tickets stay unprocessed.</item>
/// </list>
public enum ZammadImportRunStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// What kind of run this row represents. Mirrors the DB CHECK constraint
/// so the worker can route per-kind branches off one column.
public enum ZammadImportRunKind
{
    DryRun = 0,
    Import = 1,
}

/// Result the resolver assigned to one ticket. Mirrors the DB CHECK
/// constraint on <c>zammad_import_records.result</c>.
public static class ZammadImportRecordResult
{
    public const string Mapped = "mapped";
    public const string SkippedNoContact = "skipped_no_contact";
    public const string SkippedNoGroupMapping = "skipped_no_group_mapping";
    public const string SkippedNoStateMapping = "skipped_no_state_mapping";
    public const string SkippedNoPriorityMapping = "skipped_no_priority_mapping";
    public const string Failed = "failed";
}

/// Frozen snapshot of the picker's source filters at run-start. Persisted
/// as JSONB so an old run remains reproducible after the admin changes
/// the picker filters in a later session.
public sealed record ZammadImportSourceFilter(
    IReadOnlyList<long>? TicketIds,
    string? FreeText,
    IReadOnlyList<long>? GroupIds,
    IReadOnlyList<long>? StateIds,
    bool SelectAllMatching);

/// Running totals the worker bumps after each ticket. Same shape on
/// dry-run + (later) real import. Stored as JSONB on the run row so
/// the UI's progress polling reads it in one shot.
public sealed record ZammadImportTotals(
    int Processed,
    int Mapped,
    int SkippedNoContact,
    int SkippedNoGroupMapping,
    int SkippedNoStateMapping,
    int SkippedNoPriorityMapping,
    int Failed,
    int? PlannedTotal)
{
    public static ZammadImportTotals Empty(int? plannedTotal) =>
        new(0, 0, 0, 0, 0, 0, 0, plannedTotal);
}

/// Row as exposed to the admin UI on the runs-list page.
public sealed record ZammadImportRunSummary(
    Guid Id,
    ZammadImportRunKind Kind,
    ZammadImportRunStatus Status,
    Guid? StartedByUserId,
    string? StartedByDisplayName,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    ZammadImportTotals Totals,
    string? ErrorMessage);

/// Full run details — surfaced on the run-detail page.
public sealed record ZammadImportRunDetail(
    ZammadImportRunSummary Summary,
    ZammadImportSourceFilter? SourceFilter);

/// One record row in the run-detail page's table. Mapping JSON is left
/// as a raw string for the SPA to render in a code block when the admin
/// expands a row.
public sealed record ZammadImportRecordItem(
    Guid Id,
    long ZammadTicketId,
    string? ZammadTicketNumber,
    string? ZammadTicketTitle,
    string Result,
    IReadOnlyList<string> UnresolvedReasons,
    string MappingJson,
    Guid? WouldCreateTicketId,
    DateTime CreatedUtc);

/// Paged record listing.
public sealed record ZammadImportRecordPage(
    IReadOnlyList<ZammadImportRecordItem> Items,
    Guid? NextCursor);
