using Servicedesk.Domain.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public interface ITicketRepository
{
    Task<TicketPage> SearchAsync(TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct);
    Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct);
    Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct);
    /// Manual company assignment (v0.0.9 ToDo #4). Sets company_id, clears
    /// awaiting_company_assignment, stamps resolved_via='manual', bumps
    /// updated_utc, and records a CompanyAssignment timeline event with
    /// from/to metadata. Returns the refreshed detail, or null if the
    /// ticket doesn't exist.
    Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct);
    Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct);
    Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct);
    Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct);
    Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct);
    Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct);
    Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct);
    Task<int> InsertFakeBatchAsync(int count, CancellationToken ct);
}

public sealed record TicketDetail(
    Ticket Ticket,
    TicketBody Body,
    IReadOnlyList<TicketEvent> Events,
    IReadOnlyList<TicketEventPin> PinnedEvents);

public sealed record NewTicket(
    string Subject,
    string BodyText,
    string? BodyHtml,
    Guid RequesterContactId,
    Guid QueueId,
    Guid StatusId,
    Guid PriorityId,
    Guid? CategoryId,
    Guid? AssigneeUserId,
    string Source,
    Guid? CompanyId = null,
    bool AwaitingCompanyAssignment = false,
    string? CompanyResolvedVia = null);

public sealed record TicketFieldUpdate(
    Guid? QueueId = null,
    Guid? StatusId = null,
    Guid? PriorityId = null,
    Guid? CategoryId = null,
    Guid? AssigneeUserId = null,
    string? Subject = null,
    string? BodyText = null,
    string? BodyHtml = null);

public sealed record NewTicketEvent(
    string EventType,
    string? BodyText,
    string? BodyHtml,
    bool IsInternal,
    Guid? AuthorUserId,
    Guid? AuthorContactId = null,
    string? MetadataJson = null);

public sealed record UpdateTicketEvent(
    string? BodyText,
    string? BodyHtml,
    bool? IsInternal,
    Guid EditorUserId);
