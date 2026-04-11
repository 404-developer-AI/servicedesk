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
    string? Search = null,
    bool OpenOnly = false,
    DateTime? CursorUpdatedUtc = null,
    Guid? CursorId = null,
    int Limit = 50);

public sealed record TicketListItem(
    Guid Id,
    long Number,
    string Subject,
    Guid QueueId,
    string QueueName,
    Guid StatusId,
    string StatusName,
    string StatusStateCategory,
    Guid PriorityId,
    string PriorityName,
    int PriorityLevel,
    Guid RequesterContactId,
    string RequesterEmail,
    string RequesterFirstName,
    string RequesterLastName,
    Guid? AssigneeUserId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record TicketPage(
    IReadOnlyList<TicketListItem> Items,
    DateTime? NextCursorUpdatedUtc,
    Guid? NextCursorId);
