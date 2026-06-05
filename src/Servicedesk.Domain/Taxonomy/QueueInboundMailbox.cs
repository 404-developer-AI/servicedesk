namespace Servicedesk.Domain.Taxonomy;

/// One inbound mail source feeding a queue: a (mailbox, folder) pair with its
/// own Microsoft Graph delta cursor and health state. A queue can have many of
/// these (v0.0.66) — multiple mailboxes/folders all land in the same queue.
///
/// The queue keeps singular `inbound_*` columns as a denormalized mirror of the
/// FIRST source purely so the outbound from-address fallback
/// (OutboundMailboxAddress ?? inbound) keeps working without a per-source
/// lookup; this table is the authority for everything polling-related.
///
/// Field order matches the SELECT column order in QueueInboundMailboxRepository
/// (Dapper positional-record materialization is order-sensitive).
public sealed record QueueInboundMailbox(
    Guid Id,
    Guid QueueId,
    string MailboxAddress,
    string? FolderId,
    string? FolderName,
    bool PollingEnabled,
    string? DeltaLink,
    DateTime? LastPolledUtc,
    string? LastError,
    int ConsecutiveFailures,
    string? ProcessedFolderId,
    string? LastMailboxActionError,
    DateTime? LastMailboxActionErrorUtc,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
