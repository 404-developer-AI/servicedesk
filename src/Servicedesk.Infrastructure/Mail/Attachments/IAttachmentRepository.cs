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

    /// Worker call after a successful blob-write: records the hash + actual
    /// size + sniffed MIME, flips <c>processing_state</c> to Ready in one
    /// shot (6b skips the intermediate 'Stored' step — no async text-extract
    /// yet). Idempotent via <c>WHERE processing_state = 'Pending'</c>.
    Task<bool> MarkReadyAsync(Guid attachmentId, string contentHash, long sizeBytes, string mimeType, CancellationToken ct);

    /// Worker call after permanent failure (Graph 4xx, oversized, etc). The
    /// blob is never written; the row stays for audit but is excluded from
    /// the timeline renderer.
    Task MarkFailedAsync(Guid attachmentId, CancellationToken ct);
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
    string ProcessingState);
