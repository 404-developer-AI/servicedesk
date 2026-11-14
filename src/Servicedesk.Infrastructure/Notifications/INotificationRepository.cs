namespace Servicedesk.Infrastructure.Notifications;

/// Dapper-backed repository for the `user_notifications` table (v0.0.12 stap 4).
/// The table is append-only in practice: rows are inserted when a mention
/// fires, and mutated only to stamp `viewed_utc` / `acked_utc` / `email_*`
/// columns. A row is never edited or deleted by application code — ON DELETE
/// CASCADE from tickets / events handles cleanup when the parent disappears.
public interface INotificationRepository
{
    /// Bulk-insert one row per recipient in a single round-trip. Returns the
    /// inserted rows in the same order as the input so the caller can push
    /// each id onto SignalR and Graph without a second fetch.
    Task<IReadOnlyList<UserNotificationRow>> CreateManyAsync(
        IReadOnlyList<NewUserNotification> rows, CancellationToken ct);

    /// Unacked entries for the navbar-widget, most recent first.
    Task<IReadOnlyList<UserNotificationRow>> ListPendingForUserAsync(
        Guid userId, CancellationToken ct);

    /// Paginated history for the /profile/mentions page. `cursor` is the
    /// (createdUtc, id) pair of the last row from the previous page; pass
    /// null for the first page. Ordered DESC on (created_utc, id).
    Task<IReadOnlyList<UserNotificationRow>> ListHistoryForUserAsync(
        Guid userId, NotificationHistoryCursor? cursor, int limit, CancellationToken ct);

    /// Ownership-guarded fetch used by the endpoint handlers before any
    /// mutation so a hostile caller cannot ack a row that isn't theirs.
    Task<UserNotificationRow?> GetByIdForUserAsync(
        Guid id, Guid userId, CancellationToken ct);

    /// Sets viewed_utc + acked_utc to the same timestamp. No-op if the row
    /// is already acked. Returns true iff a row was updated (i.e. this call
    /// actually transitioned it).
    Task<bool> MarkViewedAsync(Guid id, Guid userId, CancellationToken ct);

    /// Sets acked_utc only (viewed_utc stays null — this is the "dismiss
    /// without navigating" path). Returns true iff a row was updated.
    Task<bool> MarkAckedAsync(Guid id, Guid userId, CancellationToken ct);

    /// Bulk-ack: sets acked_utc on every currently-unacked row for the user.
    /// Returns the number of rows updated so the endpoint can short-circuit
    /// the audit-event if nothing changed.
    Task<int> MarkAllAckedAsync(Guid userId, CancellationToken ct);

    /// Post-send status update from the mention service. `error` is null on
    /// success.
    Task MarkEmailSentAsync(Guid id, DateTime? sentUtc, string? error, CancellationToken ct);

    /// v0.1.54 — of <paramref name="userIds"/>, the users who should NOT get
    /// the mail governed by preference <paramref name="key"/>: with
    /// <paramref name="defaultOn"/> those who stored "false", otherwise everyone
    /// who did not store "true".
    Task<IReadOnlySet<Guid>> ListMailOptOutsAsync(string key, bool defaultOn, IReadOnlyCollection<Guid> userIds, CancellationToken ct);
}

/// Per-user notification preferences stored in <c>user_preferences</c>.
public static class NotificationPreferenceKeys
{
    /// "false" = no mail for @@-tags (the in-app bell still shows them).
    /// Missing row / any other value = mail on.
    public const string MentionEmail = "notify:mention-email";
    /// v0.1.54 — mail when someone replies to my internal note (default on).
    public const string ReplyEmail = "notify:note-reply-email";
    /// v0.1.54 — mail when someone likes my internal note (default off).
    public const string LikeEmail = "notify:note-like-email";

    public static readonly NotificationMailPreference MentionEmailPreference = new(MentionEmail, DefaultOn: true);
    public static readonly NotificationMailPreference ReplyEmailPreference = new(ReplyEmail, DefaultOn: true);
    public static readonly NotificationMailPreference LikeEmailPreference = new(LikeEmail, DefaultOn: false);
}

public sealed record NewUserNotification(
    Guid UserId,
    Guid? SourceUserId,
    string NotificationType,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    long EventId,
    string EventType,
    string PreviewText,
    /// v0.1.54 — set for reply / like notifications that point at a reply.
    long? ReplyId = null);

public sealed record UserNotificationRow(
    Guid Id,
    Guid UserId,
    Guid? SourceUserId,
    string? SourceUserEmail,
    string NotificationType,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    long EventId,
    string EventType,
    string PreviewText,
    DateTime CreatedUtc,
    DateTime? ViewedUtc,
    DateTime? AckedUtc,
    DateTime? EmailSentUtc,
    string? EmailError,
    long? ReplyId = null);

/// Keyset cursor for history pagination. `id` breaks ties when two rows
/// share a `created_utc` (extremely rare but possible on fast bulk inserts).
public sealed record NotificationHistoryCursor(DateTime CreatedUtc, Guid Id);
