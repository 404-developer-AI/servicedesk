namespace Servicedesk.Infrastructure.Feedback;

/// CRUD for the shared Employee Feedback board. Every user with the
/// <c>feedback_enabled</c> flag (and Admins) sees and edits all rows — the
/// authorization boundary is the flag, enforced at the endpoint layer, not a
/// per-row owner scope. The service therefore operates on rows by id without
/// a user filter; <paramref name="actorUserId"/> is recorded for audit
/// (created_by / completed_by) only.
public interface IFeedbackEntryService
{
    Task<IReadOnlyList<FeedbackEntryRow>> ListAsync(FeedbackEntryFilter filter, CancellationToken ct = default);

    /// Active Agent/Admin accounts for the board's employee dropdown.
    Task<IReadOnlyList<FeedbackEmployee>> ListEmployeesAsync(CancellationToken ct = default);

    /// Which timeline events of a ticket already have feedback logged (+ by
    /// whom), so the timeline can mark the "Log feedback" button.
    Task<IReadOnlyList<FeedbackLoggedEvent>> ListLoggedEventsAsync(Guid ticketId, CancellationToken ct = default);

    Task<FeedbackEntryRow?> GetAsync(Guid id, CancellationToken ct = default);

    /// Inserts a blank draft (today's date, empty bodies) so the inline editor
    /// has an entry id to attach pasted images to. <paramref name="targetUserId"/>
    /// defaults to the actor when null — the draft is immediately editable.
    Task<CreateFeedbackEntryResult> CreateAsync(
        Guid actorUserId, Guid? targetUserId, CancellationToken ct = default);

    /// Creates a fully-populated entry from a ticket-timeline item in one shot
    /// (source='activity'), linked to the originating ticket + event.
    Task<CreateFeedbackEntryResult> LogAsync(
        Guid actorUserId, LogFeedbackInput input, CancellationToken ct = default);

    Task<UpdateFeedbackEntryResult> UpdateAsync(
        Guid id, Guid actorUserId, FeedbackEntryInput input, CancellationToken ct = default);

    /// Inline "afgewerkt" toggle. Sets/clears completed_by + completed_utc
    /// (server time). Returns null when the row does not exist.
    Task<FeedbackEntryRow?> SetCompletedAsync(
        Guid id, Guid actorUserId, bool completed, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// Resolves a typed ticket number to a live ticket (id + subject). The id
    /// is null when no live ticket carries that number — the caller stores the
    /// number anyway, just without a clickable link.
    Task<FeedbackTicketResolution> ResolveTicketAsync(long number, CancellationToken ct = default);
}
