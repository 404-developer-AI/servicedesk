namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Lifecycle states of a KB-import run. The flow is linear up to
/// <see cref="Importing"/> and then forks into one of the three terminal
/// states. Cancellation is honoured between articles; an in-flight HTTP
/// call completes before the worker checks the column.
public enum ZammadKbImportRunStatus
{
    Pending = 0,
    Proposing = 1,
    AwaitingApproval = 2,
    Approved = 3,
    Importing = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
}

/// Admin decision per Zammad category in the proposal-review step.
/// <see cref="Create"/> is the default — the importer creates a new
/// KbSection. <see cref="Merge"/> requires a target section id; articles
/// in this Zammad category will land in the existing local section.
/// <see cref="Skip"/> excludes the category — articles under it are
/// recorded as <c>skipped_section_skipped</c>.
public enum ZammadKbSectionAction
{
    Create = 0,
    Merge = 1,
    Skip = 2,
}

/// Per-article result vocabulary written into <c>kb_import_records.result</c>.
public static class ZammadKbImportRecordResult
{
    public const string Imported = "imported";
    public const string AlreadyImported = "already_imported";
    public const string SkippedNoSectionMapping = "skipped_no_section_mapping";
    public const string SkippedNoTranslation = "skipped_no_translation";
    public const string SkippedSectionSkipped = "skipped_section_skipped";
    public const string Failed = "failed";
}

/// Running per-result counter persisted in <c>kb_import_runs.totals</c>.
/// <see cref="PlannedTotal"/> is the denominator surfaced in the progress
/// bar — null until the article picker is committed.
public sealed record ZammadKbImportTotals(
    int? PlannedTotal,
    int Processed,
    int Imported,
    int AlreadyImported,
    int SkippedNoSectionMapping,
    int SkippedNoTranslation,
    int SkippedSectionSkipped,
    int Failed)
{
    public static ZammadKbImportTotals Empty(int? plannedTotal) =>
        new(plannedTotal, 0, 0, 0, 0, 0, 0, 0);
}

/// One node in the section proposal. <see cref="ProposedTitle"/> /
/// <see cref="ProposedSlug"/> seed the create-flow; an admin can rename
/// either before applying. <see cref="TargetSectionId"/> is only set
/// when <see cref="Action"/> = <c>Merge</c>.
public sealed record ZammadKbProposalNode(
    long ZammadCategoryId,
    long? ZammadParentId,
    int Depth,
    int Position,
    string ProposedTitle,
    string ProposedSlug,
    string Action,
    Guid? TargetSectionId,
    int AnswerCount);

/// Bundle persisted on the run row + returned to the SPA.
public sealed record ZammadKbProposal(
    long KnowledgeBaseId,
    string KnowledgeBaseName,
    string DefaultLocale,
    IReadOnlyList<ZammadKbProposalNode> Nodes,
    int TotalAnswerCount);

/// One row returned from the article picker. Status is the local
/// (mapped) status, not Zammad's raw timestamps.
public sealed record ZammadKbPickerItem(
    long ZammadAnswerId,
    long ZammadCategoryId,
    string? CategoryTitle,
    string Title,
    string Status,
    bool Promoted,
    bool HasTranslation,
    DateTimeOffset? UpdatedAt);

public sealed record ZammadKbPickerPage(
    IReadOnlyList<ZammadKbPickerItem> Items,
    int Total);

/// Summary row for the runs-list. Mirrors the ticket-side shape so the
/// SPA renders both lists through the same component.
public sealed record ZammadKbImportRunSummary(
    Guid Id,
    ZammadKbImportRunStatus Status,
    Guid? StartedByUserId,
    string? StartedByDisplayName,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    long? SourceKbId,
    string? SourceKbName,
    ZammadKbImportTotals Totals,
    string? ErrorMessage);

public sealed record ZammadKbImportRecordItem(
    Guid Id,
    long ZammadAnswerId,
    long? ZammadCategoryId,
    string? ZammadTitle,
    string Result,
    IReadOnlyList<string> UnresolvedReasons,
    string MappingJson,
    Guid? TargetArticleId,
    DateTime CreatedUtc);

public sealed record ZammadKbImportRecordPage(
    IReadOnlyList<ZammadKbImportRecordItem> Items,
    Guid? NextCursor);

/// Snapshot of the article picker captured when the admin commits a
/// selection. Persisted on the run so re-opens of the run-detail page
/// can show "imported article ids X/Y/Z".
public sealed record ZammadKbArticleSelection(
    IReadOnlyList<long> AnswerIds,
    IReadOnlyDictionary<string, object?>? Filters);
