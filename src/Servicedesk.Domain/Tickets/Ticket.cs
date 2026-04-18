namespace Servicedesk.Domain.Tickets;

public sealed record Ticket(
    Guid Id,
    long Number,
    string Subject,
    Guid RequesterContactId,
    Guid? AssigneeUserId,
    Guid QueueId,
    Guid StatusId,
    Guid PriorityId,
    Guid? CategoryId,
    string Source,
    string? ExternalRef,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DueUtc,
    DateTime? FirstResponseUtc,
    DateTime? ResolvedUtc,
    DateTime? ClosedUtc,
    bool IsDeleted,
    Guid? CompanyId = null,
    bool AwaitingCompanyAssignment = false,
    string? CompanyResolvedVia = null);

public sealed record TicketBody(
    Guid TicketId,
    string BodyText,
    string? BodyHtml);

public sealed record TicketEvent(
    long Id,
    Guid TicketId,
    string EventType,
    Guid? AuthorUserId,
    Guid? AuthorContactId,
    string? AuthorName,
    string? BodyText,
    string? BodyHtml,
    string MetadataJson,
    bool IsInternal,
    DateTime CreatedUtc,
    DateTime? EditedUtc,
    Guid? EditedByUserId);

public sealed record TicketEventRevision(
    long Id,
    long EventId,
    int RevisionNumber,
    string? BodyTextBefore,
    string? BodyHtmlBefore,
    bool IsInternalBefore,
    Guid EditedByUserId,
    string? EditedByName,
    DateTime EditedUtc);

public sealed record TicketEventPin(
    long Id,
    long EventId,
    Guid TicketId,
    Guid PinnedByUserId,
    string? PinnedByName,
    string Remark,
    DateTime CreatedUtc);
