using Servicedesk.Domain.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public interface ITicketRepository
{
    Task<TicketPage> SearchAsync(TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct);
    Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct);
    Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct);
    Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct);
    Task<int> InsertFakeBatchAsync(int count, CancellationToken ct);
}

public sealed record TicketDetail(
    Ticket Ticket,
    TicketBody Body,
    IReadOnlyList<TicketEvent> Events);

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
    string Source);

public sealed record TicketFieldUpdate(
    Guid? QueueId = null,
    Guid? StatusId = null,
    Guid? PriorityId = null,
    Guid? CategoryId = null,
    Guid? AssigneeUserId = null);

public sealed record NewTicketEvent(
    string EventType,
    string? BodyText,
    string? BodyHtml,
    bool IsInternal,
    Guid? AuthorUserId);
