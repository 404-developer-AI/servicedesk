namespace Servicedesk.Infrastructure.Notifications;

/// v0.0.12 stap 4 — publishes mention-notifications to all three channels:
/// persistent DB row, SignalR push for real-time toast + navbar refresh,
/// and a mail from the ticket's queue mailbox. Transport/config failures
/// are swallowed (logged) so the originating event-post or outbound-mail
/// is never failed by a notification-side error.
public interface IMentionNotificationService
{
    Task PublishAsync(MentionNotificationSource source, CancellationToken ct);
}

public sealed record MentionNotificationSource(
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    Guid QueueId,
    long EventId,
    string EventType,
    Guid SourceUserId,
    string SourceUserEmail,
    IReadOnlyList<Guid> MentionedUserIds,
    string BodyHtml,
    string BodyText);
