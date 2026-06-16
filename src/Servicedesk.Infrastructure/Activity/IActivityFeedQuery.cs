using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Activity;

/// Read paths for the activity feed. Two shapes:
///   * <see cref="ListRecentAsync"/> — bounded "top N", used by the
///     dashboard tile. No filters.
///   * <see cref="ListAsync"/> — cursor-paginated, filterable, used by
///     the admin activity page.
/// Both take the caller's <see cref="QueueAccessScope"/> and mask (in SQL)
/// the subject/number of any ticket the caller cannot access.
public interface IActivityFeedQuery
{
    Task<IReadOnlyList<ActivityFeedEntry>> ListRecentAsync(
        QueueAccessScope viewer,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ActivityFeedPage> ListAsync(
        QueueAccessScope viewer,
        ActivityFeedFilter filter,
        CancellationToken cancellationToken = default);

    /// Used by <c>ActivityListenerWorker</c> to enrich a single new row
    /// before broadcasting. Returns the row unmasked (carrying the ticket
    /// queue id) so the broadcaster can mask it per recipient. Returns
    /// null if the row was deleted in the tiny race-window between NOTIFY
    /// and the lookup.
    Task<ActivityFeedEntry?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}

/// Masks a raw <see cref="ActivityFeedEntry"/> (as returned by
/// <see cref="IActivityFeedQuery.GetByIdAsync"/>) for a single recipient.
/// Used by the SignalR broadcaster to enforce queue-level authorization
/// on the live feed, matching the SQL masking the list reads apply.
public static class ActivityFeedMasking
{
    public static ActivityFeedEntry ForViewer(ActivityFeedEntry raw, QueueAccessScope viewer)
    {
        // Non-ticket rows (or rows whose ticket was deleted) carry no
        // queue id — nothing to mask. Visible tickets pass through
        // unchanged; the queue id is server-only ([JsonIgnore]) and is
        // deliberately NOT mutated here — the broadcaster reuses this same
        // raw instance across recipients, so clearing it would make the
        // next recipient treat the row as a non-ticket and skip masking.
        if (raw.TicketQueueId is not { } queueId || viewer.CanSee(queueId))
        {
            return raw;
        }

        return new ActivityFeedEntry
        {
            Id = raw.Id,
            OccurredUtc = raw.OccurredUtc,
            AgentId = raw.AgentId,
            AgentEmail = raw.AgentEmail,
            AgentRole = raw.AgentRole,
            EventType = raw.EventType,
            EntityType = raw.EntityType,
            EntityId = null, // strip so the client cannot deep-link
            EntityExtra = raw.EntityExtra,
            Summary = raw.Summary,
            MetadataJson = raw.MetadataJson,
            TicketNumber = null,
            TicketSubject = null,
            TicketQueueId = null,
            TicketRestricted = true,
        };
    }
}

/// Cursor-paginated filter input for the admin feed page. Null values =
/// no filter on that field. <see cref="CursorId"/> < first-page id seen
/// so far gives the next page; null = first page.
public sealed record ActivityFeedFilter(
    Guid? AgentId = null,
    string? EventType = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Search = null,
    long? CursorId = null,
    int Limit = 50);

public sealed record ActivityFeedPage(
    IReadOnlyList<ActivityFeedEntry> Items,
    long? NextCursor);
