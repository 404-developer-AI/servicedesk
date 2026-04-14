namespace Servicedesk.Infrastructure.Storage;

/// Tracks the most recent blob-store write outcome so the Health aggregator
/// can surface persistent write failures (misconfigured BlobRoot, disk full,
/// permission errors) instead of silently swallowing them in caller try/catch.
public interface IBlobStoreHealth
{
    void RecordSuccess();
    void RecordFailure(string operation, System.Exception exception);
    void Clear();
    BlobStoreHealthSnapshot Snapshot();
}

public readonly record struct BlobStoreHealthSnapshot(
    int ConsecutiveFailures,
    string? LastError,
    System.DateTime? LastErrorUtc,
    string? LastOperation,
    System.DateTime? LastSuccessUtc);
