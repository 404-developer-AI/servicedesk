namespace Servicedesk.Infrastructure.Storage;

/// Content-addressed binary store. Blobs are keyed by their SHA-256 hash,
/// so writing the same bytes twice is idempotent and costs no extra disk.
/// Implementations must make writes atomic (no partially-visible files) and
/// must reject any <paramref name="contentHash"/> that could escape the
/// configured root (path traversal).
public interface IBlobStore
{
    /// Streams <paramref name="content"/> into the store. Returns the SHA-256
    /// hex hash and the byte count observed. Safe to call concurrently with
    /// the same content: dedup is resolved by the filesystem rename.
    Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken = default);

    /// Opens a read-only stream for <paramref name="contentHash"/>. Returns
    /// <c>null</c> when the blob is not present.
    Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default);

    /// Best-effort delete. Returns <c>true</c> if the file was removed,
    /// <c>false</c> if it did not exist.
    Task<bool> DeleteAsync(string contentHash, CancellationToken cancellationToken = default);
}

public sealed record BlobWriteResult(string ContentHash, long SizeBytes);
