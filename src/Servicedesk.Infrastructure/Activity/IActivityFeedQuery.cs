namespace Servicedesk.Infrastructure.Activity;

/// Read paths for the activity feed. Two shapes:
///   * <see cref="ListRecentAsync"/> — bounded "top N", used by the
///     dashboard tile. No filters.
///   * <see cref="ListAsync"/> — cursor-paginated, filterable, used by
///     the admin activity page.
public interface IActivityFeedQuery
{
    Task<IReadOnlyList<ActivityFeedEntry>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<ActivityFeedPage> ListAsync(
        ActivityFeedFilter filter,
        CancellationToken cancellationToken = default);

    /// Used by <c>ActivityListenerWorker</c> to enrich a single new row
    /// before broadcasting. Returns null if the row was deleted in the
    /// tiny race-window between NOTIFY and the lookup.
    Task<ActivityFeedEntry?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
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
