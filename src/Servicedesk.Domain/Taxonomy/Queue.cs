namespace Servicedesk.Domain.Taxonomy;

public sealed record Queue(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    string Color,
    string Icon,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? InboundMailboxAddress = null,
    string? OutboundMailboxAddress = null,
    string? InboundFolderId = null,
    string? InboundFolderName = null,
    // v0.0.40 polish — per-queue status scope. Empty array = all
    // statuses are available (current behaviour). Non-empty =
    // dropdowns and write-paths only accept these status ids for
    // tickets in this queue. DefaultStatusId is the auto-flip target
    // when a ticket changes queue and the current status is no
    // longer allowed — null means "no auto-flip, the queue accepts
    // whatever status was on the ticket".
    //
    // Typed as IReadOnlyList<Guid> (not Guid[]) to match the
    // ComposeTemplate.QueueIds Dapper-binding convention: the
    // positional record materializer in Dapper sees `Guid[]` as a
    // mismatch against the Npgsql `uuid[]` reader and falls back to
    // "no matching constructor" — IReadOnlyList<Guid> is the shape
    // it knows how to hydrate.
    IReadOnlyList<Guid>? AllowedStatusIds = null,
    Guid? DefaultStatusId = null,
    // v0.0.60 — per-mailbox inbound polling switch. When false the
    // MailPollingService skips this queue's mailbox entirely while keeping its
    // delta-state, so re-enabling resumes from where it left off instead of
    // re-reading the whole inbox. Defaults true so existing queues keep polling.
    bool InboundPollingEnabled = true,
    // Per-queue switch for the Claude AI ticket-assist action. Defaults true so
    // existing queues keep the button when the global feature is on; admins can
    // turn it off per queue. Enforced server-side in the AI-proposal endpoint.
    bool AiAssistEnabled = true);
