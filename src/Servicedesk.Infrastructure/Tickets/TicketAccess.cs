using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Tickets;

/// The one "load a ticket the caller may see" step for staff endpoints:
/// the ticket exists, is not deleted, and sits in a queue the caller has
/// access to. Anything else is `null`, which endpoints answer with 404 —
/// never 403 — so an id probe cannot tell "missing" from "not yours".
/// <see cref="TicketMutationService.PrecheckAccessAsync"/> runs the same
/// check; loading through here means the access check cannot be skipped.
public static class TicketAccess
{
    public static async Task<Ticket?> GetAccessibleCoreAsync(
        this ITicketRepository tickets, IQueueAccessService queueAccess,
        Guid userId, string role, Guid ticketId, CancellationToken ct)
    {
        var ticket = await tickets.GetCoreAsync(ticketId, ct);
        if (ticket is null) return null;
        return await queueAccess.HasQueueAccessAsync(userId, role, ticket.QueueId, ct) ? ticket : null;
    }

    /// Same check for the full detail (body + events).
    public static async Task<TicketDetail?> GetAccessibleDetailAsync(
        this ITicketRepository tickets, IQueueAccessService queueAccess,
        Guid userId, string role, Guid ticketId, CancellationToken ct)
    {
        var detail = await tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return null;
        return await queueAccess.HasQueueAccessAsync(userId, role, detail.Ticket.QueueId, ct) ? detail : null;
    }
}
