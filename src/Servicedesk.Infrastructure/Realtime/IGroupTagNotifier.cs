namespace Servicedesk.Infrastructure.Realtime;

/// v0.1.54 — group tags realtime fan-out on the per-user notifications hub.
/// <c>GroupTagReceived</c> carries just enough for a toast; <c>GroupTagsChanged</c>
/// (payload-less) tells every member to refresh the Group tags block, e.g.
/// after a colleague acknowledged a tag.
public interface IGroupTagNotifier
{
    Task GroupTagReceivedAsync(IReadOnlyCollection<Guid> userIds, GroupTagPush payload, CancellationToken ct);
    Task GroupTagsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
}

public sealed record GroupTagPush(
    Guid Id, Guid GroupId, string GroupName, Guid TicketId, long TicketNumber, string TicketSubject,
    long EventId, string? SourceUserEmail, string PreviewText);

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullGroupTagNotifier : IGroupTagNotifier
{
    public Task GroupTagReceivedAsync(IReadOnlyCollection<Guid> userIds, GroupTagPush payload, CancellationToken ct) => Task.CompletedTask;
    public Task GroupTagsChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) => Task.CompletedTask;
}
