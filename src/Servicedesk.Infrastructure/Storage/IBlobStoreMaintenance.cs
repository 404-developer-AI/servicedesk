namespace Servicedesk.Infrastructure.Storage;

/// Housekeeping surface of the blob store, used only by the blob GC and the
/// disk sampler. Kept apart from <see cref="IBlobStore"/> so request-path
/// callers (and their test stubs) never see delete-by-scan operations.
public interface IBlobStoreMaintenance
{
    /// Absolute path of the configured blob root.
    Task<string> GetRootPathAsync(CancellationToken cancellationToken = default);

    /// Every blob file under the root (the <c>.tmp</c> staging dir and any
    /// file whose name is not a SHA-256 hex hash are skipped — never touched).
    IAsyncEnumerable<BlobFileInfo> EnumerateAsync(CancellationToken cancellationToken = default);

    /// Deletes <paramref name="contentHash"/> only if its file was last
    /// written (or dedup-touched, see <see cref="LocalFileBlobStore"/>) before
    /// <paramref name="untouchedSinceUtc"/>. Serialised against concurrent
    /// writes of the same hash, so a writer that reuses the file in the same
    /// moment either keeps it or re-creates it — never ends up pointing at a
    /// deleted file. Returns the bytes freed, or <c>null</c> when nothing was
    /// deleted.
    Task<long?> DeleteIfUntouchedSinceAsync(string contentHash, DateTime untouchedSinceUtc, CancellationToken cancellationToken = default);

    /// Staging files in <c>.tmp</c> older than <paramref name="olderThanUtc"/>
    /// (left behind by a crash mid-write). Counts only when
    /// <paramref name="delete"/> is false.
    Task<(int Files, long Bytes)> PurgeStaleTempFilesAsync(DateTime olderThanUtc, bool delete, CancellationToken cancellationToken = default);
}

public readonly record struct BlobFileInfo(string ContentHash, long SizeBytes, DateTime LastWriteUtc);
