namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// Visibility scope for a ticket search. Always resolved to the widest
/// allowed scope for a given caller at the API layer — never trusted from
/// client input. For v0.0.5 admins always get <see cref="All"/>; the
/// <see cref="Company"/> and <see cref="Own"/> scopes are present so the
/// future customer portal can reuse the same query path without a rewrite.
public enum VisibilityScope
{
    All = 0,
    Company = 1,
    Own = 2,
}

/// Search / filter input for the ticket list. All fields optional; omitted
/// ones drop out of the WHERE clause. Keyset pagination uses the
/// <see cref="CursorUpdatedUtc"/> + <see cref="CursorId"/> tuple — the last
/// row of the previous page.
public sealed record TicketQuery(
    Guid? QueueId = null,
    Guid? StatusId = null,
    Guid? PriorityId = null,
    Guid? AssigneeUserId = null,
    Guid? RequesterContactId = null,
    Guid? RequesterCompanyId = null,
    string? Search = null,
    bool OpenOnly = false,
    bool OpenFirst = false,
    // v0.0.95 — per-view "Open tickets first": prepend a state-category
    // bucket (New/Open → Pending → Resolved/Closed) so open tickets sort
    // above pending ones regardless of the secondary sort. Finer-grained
    // than OpenFirst, which only pushes Resolved/Closed below the rest.
    bool StateBucketSort = false,
    string? SortField = null,
    string? SortDirection = null,
    bool PriorityFloat = false,
    int? Offset = null,
    DateTime? CursorUpdatedUtc = null,
    Guid? CursorId = null,
    int Limit = 50,
    IReadOnlyList<Guid>? AccessibleQueueIds = null,
    // v0.0.40 polish — multi-select filters from a saved view. When a
    // list is non-empty, the singular counterpart is ignored and the
    // SQL uses `= ANY(@<List>)`. Singular fields stay around for the
    // sidebar's ad-hoc filter dropdowns + legacy URLs.
    IReadOnlyList<Guid>? QueueIds = null,
    IReadOnlyList<Guid>? StatusIds = null,
    IReadOnlyList<Guid>? PriorityIds = null,
    // v0.0.105 — restrict to project tickets, regardless of queue. Used by
    // the "Project tickets only" view filter so one saved view shows every
    // project across the whole install (queue access still applies).
    bool ProjectsOnly = false,
    // v0.1.17 — Call-back / Research floats (per view, below the Priority
    // float in fixed precedence) and the matching "only" view filters.
    bool CallbackFloat = false,
    bool ResearchFloat = false,
    bool CallbacksOnly = false,
    bool ResearchOnly = false,
    // v0.1.18 — per-view search box. "Full" mode: prefix tsquery text (built
    // by TicketTsQuery.BuildPrefix) over subject, description and every
    // article, plus an optional exact ticket-number probe. Independent of
    // the legacy Search filter a view may carry; both AND together.
    string? FullSearchTsQuery = null,
    long? FullSearchNumber = null,
    // v0.1.18 — "Columns" mode server fallback (list truncated): substring
    // match on whitelisted visible columns, see TicketColumnSearch.
    string? ColumnSearch = null,
    IReadOnlyList<string>? ColumnSearchFields = null,
    // v0.1.18 — compute the "Time logged" column (sum of every timesheet
    // entry on the ticket). Only when the column is visible or sorted on.
    bool IncludeTimeLogged = false);

public sealed record TicketListItem(
    Guid Id,
    long Number,
    string Subject,
    Guid QueueId,
    string QueueName,
    Guid StatusId,
    string StatusName,
    string StatusColor,
    string StatusStateCategory,
    Guid PriorityId,
    string PriorityName,
    int PriorityLevel,
    string PriorityColor,
    bool PriorityIsDefault,
    Guid RequesterContactId,
    string RequesterEmail,
    string RequesterFirstName,
    string RequesterLastName,
    Guid? RequesterCompanyId,
    string? CompanyName,
    Guid? AssigneeUserId,
    string? AssigneeEmail,
    Guid? CategoryId,
    string? CategoryName,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DueUtc,
    // v0.0.74 — snooze/pending-till timestamp. Non-null = currently pending
    // until this moment; the scheduler clears it on elapse, so in practice a
    // value here is always in the future. Surfaced as an opt-in list column +
    // sort field. Column order matches the ListSelect projection (Dapper
    // positional-record rule).
    DateTime? PendingTillUtc,
    bool AwaitingCompanyAssignment = false,
    string? CompanyResolvedVia = null,
    // v0.0.39 — surfaced so the list row can render the type-badge
    // alongside the priority/category pills.
    Guid TicketTypeId = default,
    // v0.0.103 - denormalized checklist progress (sum over the ticket's
    // attached checklists, required items only) for the list chip.
    int ChecklistRequiredTotal = 0,
    int ChecklistRequiredDone = 0,
    // v0.1.17 — Call-back / Research flags for the float buckets + row accent.
    bool IsCallback = false,
    bool IsResearch = false,
    // v0.1.18 — total logged minutes (all agents, invoiced or not); null
    // when the query did not ask for it (IncludeTimeLogged = false).
    int? TimeLoggedMinutes = null);

public sealed record TicketPage(
    IReadOnlyList<TicketListItem> Items,
    DateTime? NextCursorUpdatedUtc,
    Guid? NextCursorId,
    int? NextOffset = null);
