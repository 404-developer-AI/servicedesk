using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Tickets;

/// The one rule for the status and priority of a ticket nobody picked them
/// for (mail intake, portal registration, the maintenance planner): the
/// active row flagged default, else the first active row.
public static class TicketDefaults
{
    public static Status? PickStatus(IEnumerable<Status> statuses) =>
        statuses.FirstOrDefault(s => s.IsDefault && s.IsActive) ?? statuses.FirstOrDefault(s => s.IsActive);

    public static Priority? PickPriority(IEnumerable<Priority> priorities) =>
        priorities.FirstOrDefault(p => p.IsDefault && p.IsActive) ?? priorities.FirstOrDefault(p => p.IsActive);

    /// Null when no active status or priority exists.
    public static async Task<(Guid StatusId, Guid PriorityId)?> ResolveAsync(ITaxonomyRepository taxonomy, CancellationToken ct)
    {
        var status = PickStatus(await taxonomy.ListStatusesAsync(ct));
        var priority = PickPriority(await taxonomy.ListPrioritiesAsync(ct));
        return status is null || priority is null ? null : (status.Id, priority.Id);
    }
}
