namespace Servicedesk.Infrastructure.Insights.Rewind;

/// One ticket as it stood in a view at capture time. Stored in
/// <c>rewind_snapshots.items</c> in displayed order; <see cref="Group"/> is
/// the key of the group it showed under. The subject, requester and company
/// are frozen on purpose: a clear subject is itself a work-order rule, so
/// the title at that moment is part of what Rewind shows.
public sealed record RewindItem(
    Guid Id,
    long Number,
    string Subject,
    Guid QueueId,
    string QueueName,
    Guid StatusId,
    string StatusName,
    string StatusColor,
    string StateCategory,
    string PriorityName,
    string PriorityColor,
    bool PriorityIsDefault,
    bool IsCallback,
    bool IsResearch,
    string Requester,
    string? CompanyName,
    Guid? AssigneeUserId,
    string? AssigneeEmail,
    DateTime CreatedUtc,
    DateTime? PendingTillUtc,
    string Group);

/// A ticket that entered or left a view between two snapshots. <see cref="R"/>
/// is the reason a ticket left: <c>closed</c> (resolved, closed or merged),
/// <c>queue</c> (moved to another queue) or <c>other</c>; null for arrivals.
public sealed record RewindChange(Guid Id, Guid Q, long N, string S, string? R = null);

public static class RewindLeaveReason
{
    public const string Closed = "closed";
    public const string Queue = "queue";
    public const string Other = "other";
}

/// A group header in displayed order (float buckets first).
public sealed record RewindGroup(string Key, string Label, string? Color);

/// Tally of one (group, queue) pair — what the chart sums per viewer.
public sealed record RewindCount(string G, Guid Q, int N);

public sealed record RewindTrackedView(Guid Id, string Name, string FiltersJson, string DisplayConfigJson);

/// The latest stored snapshot of a view, for change detection.
public sealed record RewindLatest(long Id, DateTime CapturedUtc, DateTime LastSeenUtc, int IntervalMinutes, string ContentHash);

/// A snapshot row without its items, for the chart.
public sealed record RewindSeriesRow(
    DateTime CapturedUtc, DateTime LastSeenUtc, int IntervalMinutes, string GroupsJson, string CountsJson,
    string? AddedJson = null, string? RemovedJson = null);

public sealed record RewindSnapshotRow(
    DateTime CapturedUtc, DateTime LastSeenUtc, int IntervalMinutes, bool Truncated,
    string GroupsJson, string ItemsJson, string? AddedJson = null, string? RemovedJson = null);

/// A new capture, ready to store.
public sealed record RewindCapture(
    Guid ViewId, DateTime SlotUtc, int IntervalMinutes, int TicketCount, bool Truncated,
    string ContentHash, string GroupsJson, string CountsJson, string ItemsJson,
    string? AddedJson = null, string? RemovedJson = null);
