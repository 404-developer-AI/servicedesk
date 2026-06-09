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
    IReadOnlyList<Guid>? PriorityIds = null);

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
    Guid TicketTypeId = default);

public sealed record TicketPage(
    IReadOnlyList<TicketListItem> Items,
    DateTime? NextCursorUpdatedUtc,
    Guid? NextCursorId,
    int? NextOffset = null);
