using Servicedesk.Domain.Taxonomy;

namespace Servicedesk.Infrastructure.Mail.Polling;

/// Owns the queue_inbound_mailboxes table: the (mailbox, folder) sources that
/// feed a queue, plus each source's Graph delta cursor and health state. A queue
/// can have many sources (v0.0.66). Config CRUD is admin-driven via the queue
/// editor; the state mutators are driven by the polling loop / finalizer.
public interface IQueueInboundMailboxRepository
{
    // ---------- Reads ----------

    /// Every source across all queues, with state — drives the poller loop,
    /// the health card and the Settings → Mail list.
    Task<IReadOnlyList<QueueInboundMailbox>> ListAllAsync(CancellationToken ct);

    /// Sources for a single queue (config + state), ordered oldest-first.
    Task<IReadOnlyList<QueueInboundMailbox>> ListByQueueAsync(Guid queueId, CancellationToken ct);

    Task<QueueInboundMailbox?> GetAsync(Guid id, CancellationToken ct);

    /// Distinct mailbox addresses across all sources — used by ingest
    /// loop-prevention to recognise our own mailboxes.
    Task<IReadOnlyList<string>> ListAllMailboxAddressesAsync(CancellationToken ct);

    /// The queueId that already owns the given (mailbox, folder) via a source
    /// other than <paramref name="excludeSourceId"/>, or null when free. Only
    /// meaningful when a folder is selected (exclusivity index is partial).
    Task<Guid?> FindConflictingQueueAsync(string mailbox, string? folderId, Guid? excludeSourceId, CancellationToken ct);

    // ---------- Config writes ----------

    Task<QueueInboundMailbox> AddAsync(Guid queueId, string mailbox, string? folderId, string? folderName, bool pollingEnabled, CancellationToken ct);

    /// Updates config. Wipes the delta cursor + failure backoff + cached
    /// processed-folder id when the mailbox or folder actually changes (a Graph
    /// delta token is bound to the folder it was issued for). Returns false when
    /// the row no longer exists.
    Task<bool> UpdateConfigAsync(Guid id, string mailbox, string? folderId, string? folderName, bool pollingEnabled, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    Task<bool> SetPollingAsync(Guid id, bool enabled, CancellationToken ct);

    /// Re-syncs queues.inbound_* to the queue's first source (or NULL when the
    /// queue has no sources). Keeps the outbound from-address fallback working.
    Task RefreshMirrorAsync(Guid queueId, CancellationToken ct);

    // ---------- State writes (poller / finalizer) ----------

    Task SaveSuccessAsync(Guid id, string? deltaLink, DateTime polledUtc, CancellationToken ct);
    Task SaveFailureAsync(Guid id, string error, DateTime polledUtc, CancellationToken ct);
    Task ResetFailuresAsync(Guid id, CancellationToken ct);
    Task SaveProcessedFolderIdAsync(Guid id, string folderId, CancellationToken ct);
    Task SaveMailboxActionErrorAsync(Guid id, string error, DateTime occurredUtc, CancellationToken ct);
    Task ClearMailboxActionErrorAsync(Guid id, CancellationToken ct);
}
