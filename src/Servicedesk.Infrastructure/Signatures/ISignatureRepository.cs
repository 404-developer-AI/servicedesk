using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

/// CRUD + send-path lookups for email signatures, their image assets, and the
/// queue→signature bindings. Hand-written parameterized SQL (Dapper-first).
public interface ISignatureRepository
{
    Task<IReadOnlyList<Signature>> ListAsync(CancellationToken ct);
    Task<Signature?> GetAsync(Guid id, CancellationToken ct);

    Task<Guid> CreateAsync(
        string name, SignatureDesign design, bool isSystem, bool enabled,
        int sortOrder, Guid? createdBy, CancellationToken ct);

    Task<bool> UpdateAsync(
        Guid id, string name, SignatureDesign design, bool isSystem, bool enabled,
        int sortOrder, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// Send-path lookup: the enabled signature bound to <paramref name="queueId"/>,
    /// or null if the mailbox has no (enabled) signature.
    Task<Signature?> ResolveForQueueAsync(Guid queueId, CancellationToken ct);

    // ---- assets ----
    Task<IReadOnlyList<SignatureAsset>> ListAssetsAsync(Guid signatureId, CancellationToken ct);
    Task<SignatureAsset?> GetAssetAsync(Guid assetId, CancellationToken ct);
    Task<Guid> AddAssetAsync(
        Guid signatureId, string contentHash, string mimeType,
        string originalFilename, long sizeBytes, CancellationToken ct);
    Task<bool> DeleteAssetAsync(Guid assetId, CancellationToken ct);

    // ---- mailbox (queue) bindings ----
    Task<IReadOnlyList<SignatureMailbox>> ListMailboxesAsync(CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListQueuesForSignatureAsync(Guid signatureId, CancellationToken ct);

    /// Upserts the binding: a queue maps to exactly one signature (queue_id PK).
    Task SetMailboxAsync(Guid queueId, Guid signatureId, CancellationToken ct);
    Task<bool> ClearMailboxAsync(Guid queueId, CancellationToken ct);

    /// Replaces the full set of queues a signature is bound to in one call
    /// (used by the builder's "active on these mailboxes" multi-select).
    Task SetQueuesForSignatureAsync(Guid signatureId, IReadOnlyList<Guid> queueIds, CancellationToken ct);
}
