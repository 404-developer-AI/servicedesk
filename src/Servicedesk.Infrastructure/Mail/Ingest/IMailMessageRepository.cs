namespace Servicedesk.Infrastructure.Mail.Ingest;

public interface IMailMessageRepository
{
    Task<MailMessageRow?> GetByMessageIdAsync(string internetMessageId, CancellationToken ct);

    Task<MailMessageRow?> GetByIdAsync(Guid id, CancellationToken ct);

    /// Given a list of RFC-822 Message-IDs (typically In-Reply-To + References
    /// split), return the first matching ticket_id, or null if no known mail
    /// references any of them.
    Task<Guid?> FindTicketIdByReferencesAsync(IReadOnlyList<string> messageIds, CancellationToken ct);

    /// Inserts the mail_messages row, its recipients, and any attachments
    /// (with a matching 'Ingest' job per attachment) atomically. Returns the
    /// generated mail id.
    Task<Guid> InsertAsync(
        NewMailMessage row,
        IReadOnlyList<NewMailRecipient> recipients,
        IReadOnlyList<NewMailAttachment> attachments,
        CancellationToken ct);

    Task AttachToTicketAsync(Guid mailId, Guid ticketId, long eventId, CancellationToken ct);

    /// Sets <c>mailbox_moved_utc</c> to record that the message has been
    /// relocated out of the Inbox (or that we've given up and are treating
    /// it as "done" — e.g. Graph 404 because a user moved it manually).
    /// Idempotent.
    Task MarkMailboxMovedAsync(Guid mailId, DateTime utc, CancellationToken ct);

    /// Returns mails that are ready to be moved to the processed folder:
    /// ticket-attached, not yet moved, and every attachment in
    /// <c>Ready</c> state (or no attachments at all). Limit caps the batch
    /// per sweeper tick.
    Task<IReadOnlyList<FinalizeCandidate>> ListReadyForFinalizeAsync(int limit, CancellationToken ct);

    /// Fast-path variant: if <paramref name="mailId"/> is ready for finalize,
    /// returns its candidate; otherwise null. Used by the attachment worker
    /// after Complete/DeadLetter to avoid full sweeps.
    Task<FinalizeCandidate?> GetIfReadyForFinalizeAsync(Guid mailId, CancellationToken ct);
}

public sealed record MailMessageRow(
    Guid Id,
    string MessageId,
    string? InReplyTo,
    string Subject,
    string FromAddress,
    string FromName,
    string MailboxAddress,
    DateTime ReceivedUtc,
    string? RawEmlBlobHash,
    string? BodyHtmlBlobHash,
    string BodyText,
    Guid? TicketId,
    long? TicketEventId,
    string? GraphMessageId,
    DateTime? MailboxMovedUtc);

public sealed record NewMailMessage(
    string MessageId,
    string? InReplyTo,
    string? References,
    string Subject,
    string FromAddress,
    string FromName,
    string MailboxAddress,
    DateTime ReceivedUtc,
    string? RawEmlBlobHash,
    string? BodyHtmlBlobHash,
    string BodyText,
    string GraphMessageId);

/// A mail eligible for the mailbox-move finalizer step.
public sealed record FinalizeCandidate(
    Guid MailId,
    string GraphMessageId,
    string MailboxAddress);

public sealed record NewMailRecipient(string Kind, string Address, string DisplayName);

/// Metadata for a Graph file-attachment on an inbound mail. The repository
/// inserts the <c>attachments</c> row in state <c>Pending</c> (no content_hash
/// yet) and enqueues a matching Ingest job so the worker can download bytes.
public sealed record NewMailAttachment(
    string GraphAttachmentId,
    string Mailbox,
    string GraphMessageId,
    string FileName,
    string MimeType,
    long Size,
    bool IsInline,
    string? ContentId);
