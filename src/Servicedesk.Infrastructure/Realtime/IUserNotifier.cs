namespace Servicedesk.Infrastructure.Realtime;

/// Per-user SignalR fan-out for notification-framework events. Parallel to
/// <see cref="ITicketListNotifier"/> but scoped to individual recipients
/// (group key <c>user:{userId}</c>) so a tagged agent only receives their
/// own notifications — never the noise of every agent's inbox.
/// <para>
/// The payload is deliberately minimal: the client uses it to invalidate
/// the pending-notifications query and to show an immediate toast. The
/// navbar / history page render from the freshly-invalidated data, not
/// from this payload, so we never ship sensitive body contents over the
/// push channel — just enough metadata to compose the toast.
/// </para>
public interface IUserNotifier
{
    Task NotifyMentionAsync(Guid userId, UserNotificationPush payload, CancellationToken ct);
}

public sealed record UserNotificationPush(
    Guid Id,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    string? SourceUserEmail,
    long EventId,
    string EventType,
    string PreviewText,
    DateTime CreatedUtc);

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullUserNotifier : IUserNotifier
{
    public Task NotifyMentionAsync(Guid userId, UserNotificationPush payload, CancellationToken ct)
        => Task.CompletedTask;
}
