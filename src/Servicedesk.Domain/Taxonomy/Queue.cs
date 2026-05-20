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
    Guid? DefaultStatusId = null);
