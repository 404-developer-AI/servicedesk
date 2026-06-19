namespace Servicedesk.Infrastructure.Realtime;

/// Per-user SignalR fan-out for the Timesheet → Comments feature. Pushes a
/// minimal <c>TimesheetCommentReceived</c> signal to the linked recipient's
/// <c>user:{userId}</c> group so their UI invalidates the inbox + unread-count
/// queries and lights the red dots. Deliberately separate from
/// <see cref="IUserNotifier"/> (the @@-mention raamwerk): timesheet comments
/// stay inside the Timesheet UI and never touch the global notification bell.
/// <para>
/// The payload carries no message body — just the ticket reference, enough to
/// route a refresh. The client re-reads the authoritative data from the
/// freshly-invalidated queries.
/// </para>
public interface ITimesheetCommentNotifier
{
    Task NotifyCommentAsync(Guid userId, TimesheetCommentPush payload, CancellationToken ct);
}

public sealed record TimesheetCommentPush(
    Guid ThreadId,
    long TicketNumber,
    string OriginContext,
    string? SourceUserEmail);

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullTimesheetCommentNotifier : ITimesheetCommentNotifier
{
    public Task NotifyCommentAsync(Guid userId, TimesheetCommentPush payload, CancellationToken ct)
        => Task.CompletedTask;
}
