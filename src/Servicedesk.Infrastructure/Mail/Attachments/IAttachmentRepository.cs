namespace Servicedesk.Infrastructure.Mail.Attachments;

/// Metadata-layer operations on <c>attachments</c>. Used by the worker to
/// promote rows through the Pending→Stored→Ready state machine, and by the
/// download endpoint + HTML renderer to resolve a row back to its blob.
public interface IAttachmentRepository
{
    Task<AttachmentRow?> GetByIdAsync(Guid id, CancellationToken ct);

    /// Every Ready attachment owned by a given mail. Used by the HTML renderer
    /// to resolve <c>cid:</c> references and by the timeline to list files.
    Task<IReadOnlyList<AttachmentRow>> ListByMailAsync(Guid mailId, CancellationToken ct);

    /// Ready attachments linked to a specific ticket-event (Note / Comment /
    /// reply). Used by the timeline-enricher to attach a download strip to
    /// posts the same way it does for inbound mail. <c>event_id</c> is set
    /// at post-submit time; nothing else writes to the column.
    Task<IReadOnlyList<AttachmentRow>> ListByEventAsync(long eventId, CancellationToken ct);

    /// Worker call after a successful blob-write: records the hash + actual
    /// size + sniffed MIME, flips <c>processing_state</c> to Ready in one
    /// shot (6b skips the intermediate 'Stored' step — no async text-extract
    /// yet). Idempotent via <c>WHERE processing_state = 'Pending'</c>.
    Task<bool> MarkReadyAsync(Guid attachmentId, string contentHash, long sizeBytes, string mimeType, CancellationToken ct);

    /// Worker call after permanent failure (Graph 4xx, oversized, etc). The
    /// blob is never written; the row stays for audit but is excluded from
    /// the timeline renderer.
    Task MarkFailedAsync(Guid attachmentId, CancellationToken ct);

    /// Insert a fully-stored, user-uploaded attachment. The blob has already
    /// been written via <c>IBlobStore</c>, so the row lands as <c>Ready</c>.
    /// <paramref name="ownerKind"/> is <c>'Ticket'</c> while the file is
    /// staged (no event/mail picked yet) and is later flipped to <c>'Mail'</c>
    /// or to a non-null <c>event_id</c> via <see cref="ReassignToMailAsync"/>
    /// or <see cref="ReassignToEventAsync"/>.
    Task<Guid> CreateUploadedAsync(NewUploadedAttachment input, CancellationToken ct);

    /// Move a batch of staged attachments onto a ticket-event in one update.
    /// Only flips rows that are still owned by the same ticket and have no
    /// existing <c>event_id</c> — prevents an attacker who knows another
    /// ticket's attachment ids from re-parenting them onto their own ticket.
    /// Returns the number of rows actually re-assigned.
    Task<int> ReassignToEventAsync(IReadOnlyList<Guid> attachmentIds, Guid ticketId, long eventId, CancellationToken ct);

    /// Move a batch of staged attachments onto a mail-message row + stamp the
    /// MIME Content-Id where supplied (inline images get a synthetic cid the
    /// outbound MIME body references; non-inline attachments pass null).
    /// Same ownership-guard as <see cref="ReassignToEventAsync"/>. Also stamps
    /// <paramref name="ticketEventId"/> so the timeline-enricher can locate
    /// outbound-mail attachments via either side of the join.
    Task<int> ReassignToMailAsync(IReadOnlyList<AttachmentReassignToMail> assignments, Guid ticketId, Guid mailMessageId, long ticketEventId, CancellationToken ct);
}

public sealed record AttachmentRow(
    Guid Id,
    Guid OwnerId,
    string OwnerKind,
    string? ContentHash,
    long SizeBytes,
    string MimeType,
    string OriginalFilename,
    bool IsInline,
    string? ContentId,
    string ProcessingState,
    long? EventId = null);

public sealed record NewUploadedAttachment(
    Guid TicketId,
    string ContentHash,
    long SizeBytes,
    string MimeType,
    string OriginalFilename);

public sealed record AttachmentReassignToMail(
    Guid AttachmentId,
    string? ContentId,
    bool IsInline);
