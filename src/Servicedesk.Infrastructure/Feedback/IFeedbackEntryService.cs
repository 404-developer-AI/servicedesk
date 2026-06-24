namespace Servicedesk.Infrastructure.Feedback;

/// CRUD for the Employee Feedback board. Two access scopes (v0.0.90):
/// FULL users (<c>feedback_enabled</c>, plus Admins) see and edit every row —
/// the shared board; RESTRICTED users (<c>feedback_own_only</c>) may log
/// feedback but only see/edit rows they created, and management fields stay
/// read-only for them. The endpoint layer resolves the scope and passes
/// <paramref name="ownOnly"/> + <paramref name="actorUserId"/> into the
/// row-scoping methods. <paramref name="actorUserId"/> is also recorded for
/// audit (created_by / completed_by).
public interface IFeedbackEntryService
{
    /// Lists board rows. When <paramref name="ownOnly"/>, only rows created by
    /// <paramref name="actorUserId"/> are returned.
    Task<IReadOnlyList<FeedbackEntryRow>> ListAsync(
        FeedbackEntryFilter filter, Guid actorUserId, bool ownOnly, CancellationToken ct = default);

    /// Active Agent/Admin accounts for the board's employee dropdown.
    Task<IReadOnlyList<FeedbackEmployee>> ListEmployeesAsync(CancellationToken ct = default);

    /// Which timeline events of a ticket already have feedback logged (+ by
    /// whom), so the timeline can mark the "Log feedback" button. When
    /// <paramref name="ownOnly"/>, only the actor's own loggings are counted —
    /// a restricted user never learns a colleague logged feedback on an event.
    Task<IReadOnlyList<FeedbackLoggedEvent>> ListLoggedEventsAsync(
        Guid ticketId, Guid actorUserId, bool ownOnly, CancellationToken ct = default);

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

    /// Full-row update. When <paramref name="ownOnly"/>, the row must have been
    /// created by <paramref name="actorUserId"/> (else NotFound, hiding its
    /// existence) and the management fields (management remarks + completed)
    /// are preserved from the stored row — a restricted user cannot change
    /// them.
    Task<UpdateFeedbackEntryResult> UpdateAsync(
        Guid id, Guid actorUserId, FeedbackEntryInput input, bool ownOnly, CancellationToken ct = default);

    /// Inline "afgewerkt" toggle. Sets/clears completed_by + completed_utc
    /// (server time). Returns null when the row does not exist. A management
    /// action — restricted (own-only) users are blocked at the endpoint layer.
    Task<FeedbackEntryRow?> SetCompletedAsync(
        Guid id, Guid actorUserId, bool completed, CancellationToken ct = default);

    /// Deletes a row. When <paramref name="ownOnly"/>, only a row created by
    /// <paramref name="actorUserId"/> is removed (others report not-found).
    Task<bool> DeleteAsync(Guid id, Guid actorUserId, bool ownOnly, CancellationToken ct = default);

    /// Resolves a typed ticket number to a live ticket (id + subject). The id
    /// is null when no live ticket carries that number — the caller stores the
    /// number anyway, just without a clickable link.
    Task<FeedbackTicketResolution> ResolveTicketAsync(long number, CancellationToken ct = default);
}
