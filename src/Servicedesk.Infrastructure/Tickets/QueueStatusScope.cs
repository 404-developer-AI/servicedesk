using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Tickets;

/// The "status must sit in the queue's allowed-status list" rule, in one
/// place. Agent writes (<see cref="TicketMutationService"/>, single and bulk)
/// and the trigger "Set status" action both ask this service, so a trigger
/// can never set a status an agent could not set on the same ticket.
public interface IQueueStatusScope
{
    /// True when <paramref name="statusId"/> is allowed in the queue: the
    /// queue has no allowed-status list (empty = every status), or the list
    /// contains it. An unknown queue is not judged here (callers already
    /// handle missing tickets/queues) and returns true.
    Task<bool> IsStatusAllowedAsync(Guid queueId, Guid statusId, CancellationToken ct);
}

internal sealed class QueueStatusScope : IQueueStatusScope
{
    private readonly ITaxonomyRepository _taxonomy;

    public QueueStatusScope(ITaxonomyRepository taxonomy) => _taxonomy = taxonomy;

    public async Task<bool> IsStatusAllowedAsync(Guid queueId, Guid statusId, CancellationToken ct)
    {
        var queue = await _taxonomy.GetQueueAsync(queueId, ct);
        return queue?.AllowedStatusIds is not { Count: > 0 } allowed || allowed.Contains(statusId);
    }
}
