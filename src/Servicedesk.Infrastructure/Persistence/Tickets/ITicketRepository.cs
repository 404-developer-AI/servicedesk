using Servicedesk.Domain.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public interface ITicketRepository
{
    Task<TicketPage> SearchAsync(TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct);
    Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct);
    Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct);
    /// Manual company assignment (v0.0.9 ToDo #4). Sets company_id, clears
    /// awaiting_company_assignment, stamps resolved_via='manual', bumps
    /// updated_utc, and records a CompanyAssignment timeline event with
    /// from/to metadata. Returns the refreshed detail, or null if the
    /// ticket doesn't exist.
    Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct);
    /// Switches the ticket's requester to a different contact and applies the
    /// company resolution that the caller already computed (so the endpoint
    /// stays in charge of the policy). Writes a RequesterChange timeline event
    /// with from/to contact + company metadata. Returns the refreshed detail,
    /// or null if the ticket doesn't exist.
    Task<TicketDetail?> ChangeRequesterAsync(
        Guid ticketId,
        Guid newContactId,
        Guid? newCompanyId,
        bool awaitingCompanyAssignment,
        string? companyResolvedVia,
        Guid actorUserId,
        CancellationToken ct);
    Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct);
    /// True when the ticket's title has already been reviewed at first open
    /// (title_reviewed_utc is set). Used by the first-open gate probe to
    /// suppress the dialog after the one-time review. Returns true for a
    /// missing ticket so a vanished ticket never re-surfaces the gate.
    Task<bool> IsTitleReviewedAsync(Guid ticketId, CancellationToken ct);
    /// Atomically stamps title_reviewed_utc + title_reviewed_by_user_id,
    /// but only when the ticket exists and has not been reviewed yet.
    /// Returns true when this call performed the stamp (the caller "won"
    /// the race and should run the gate's confirmation actions), false when
    /// the ticket was already reviewed or doesn't exist. Race-safe: two
    /// agents confirming the same first-open gate concurrently see exactly
    /// one true.
    Task<bool> MarkTitleReviewedAsync(Guid ticketId, Guid actorUserId, CancellationToken ct);
    Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct);
    Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct);
    Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct);
    Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct);
    Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct);
    /// Cheap existence check used by the attachment download endpoint to
    /// verify an attachment owned by a ticket-event actually belongs to the
    /// ticket the agent is viewing — returns false when the join doesn't
    /// hold so the endpoint can 404 instead of leaking the pair.
    Task<bool> EventBelongsToTicketAsync(Guid ticketId, long eventId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct);
    Task<int> InsertFakeBatchAsync(int count, CancellationToken ct);

    /// Lightweight ticket numbers + subjects for the merge / link-parent picker
    /// autocomplete. Filters out merged/deleted tickets and the source ticket
    /// itself; queue access is enforced by the caller passing AccessibleQueueIds
    /// (null = admin). When <paramref name="recentForUserId"/> is supplied AND no
    /// search term is given, the result leads with the tickets that user most
    /// recently opened (newest first), backfilled with the global most-recently-
    /// updated list — so the link-parent dialog opens on the user's own context.
    /// When <paramref name="projectsOnly"/> is true only open project tickets
    /// (is_project, state category not Resolved/Closed) are returned — used by
    /// the link-to-project dialog (v0.0.104).
    Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(
        string? search,
        Guid excludeTicketId,
        IReadOnlyCollection<Guid>? accessibleQueueIds,
        Guid? recentForUserId,
        int limit,
        CancellationToken ct,
        bool projectsOnly = false);

    /// v0.0.101 — everything the detail view needs *around* the ticket, in one
    /// round-trip: merge sources ("Merged from #A, #B"), merge target + actor,
    /// split parent + actor, split children ("Split into #A, #B"), linked
    /// parent summary, linked child tickets, and the company-alert source
    /// (ticket's own frozen company, else the requester's active primary
    /// company). Replaces six single-purpose lookups that each opened their
    /// own connection on every ticket open. Returns null when the ticket does
    /// not exist (or is deleted).
    Task<TicketDetailRelations?> GetDetailRelationsAsync(Guid ticketId, CancellationToken ct);

    /// Performs the merge in a single transaction. Re-points all events,
    /// mail messages, pinned events, mention notifications and intake forms
    /// from <paramref name="sourceTicketId"/> onto <paramref name="targetTicketId"/>;
    /// stores the original source body as a Comment event on the target so the
    /// requester's first message is not lost; flips the source ticket to status
    /// "Merged" with merged_into / merged_utc / merged_by_user_id stamped.
    /// Returns the moved-event count, or null on validation failure.
    Task<MergeResult?> MergeAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        Guid actorUserId,
        bool acknowledgedCrossCustomer,
        CancellationToken ct);


    /// Splits a multi-question mail off into a fresh ticket. Looks up the
    /// source mail event on <paramref name="sourceTicketId"/>, creates a new
    /// ticket using the source's requester/company plus the queue/priority/status
    /// defaults, copies the mail body into the new ticket's description, and
    /// writes a SystemNote event on each side referencing the other. The
    /// caller passes <paramref name="overrideBodyHtml"/> (and optionally
    /// <paramref name="overrideBodyText"/>) when the raw event body still
    /// contains MIME `cid:` references — the endpoint runs the mail-timeline
    /// enricher first so inline images keep resolving against the source
    /// mail's attachment URLs. Returns null when the source mail event isn't
    /// found, isn't a MailReceived event, or doesn't belong to the source.
    Task<SplitResult?> SplitAsync(
        Guid sourceTicketId,
        long sourceMailEventId,
        string newSubject,
        Guid actorUserId,
        string? overrideBodyHtml,
        string? overrideBodyText,
        CancellationToken ct);


    /// Links <paramref name="ticketId"/> as a sub-ticket of
    /// <paramref name="parentTicketId"/>. Writes a ParentLinked timeline
    /// event on the child. Returns the failure reason on validation error
    /// (self-link, cycle, parent doesn't exist, …) so the endpoint can
    /// translate it into the right HTTP status.
    Task<LinkParentResult> LinkParentAsync(
        Guid ticketId,
        Guid parentTicketId,
        Guid actorUserId,
        CancellationToken ct);

    /// Clears `parent_ticket_id` on <paramref name="ticketId"/> and writes
    /// a ParentUnlinked event. Returns false when the ticket doesn't
    /// exist OR has no parent (idempotent no-op the caller can 404 on).
    Task<bool> UnlinkParentAsync(Guid ticketId, Guid actorUserId, CancellationToken ct);
}

public sealed class LinkedChildTicket
{
    public Guid Id { get; set; }
    public long Number { get; set; }
}

public sealed record ParentTicketSummary(
    Guid ParentTicketId,
    long ParentNumber,
    string? LinkedByName,
    DateTime LinkedUtc);

/// v0.0.101 — one-round-trip bundle for the ticket detail endpoint (see
/// ITicketRepository.GetDetailRelationsAsync). Numbers are pre-stringified
/// where the API contract already returns strings.
public sealed record TicketDetailRelations(
    IReadOnlyList<long> MergedSourceTicketNumbers,
    string? MergedByUserName,
    string? MergedIntoTicketNumber,
    string? SplitFromTicketNumber,
    string? SplitFromUserName,
    IReadOnlyList<SplitChildTicket> SplitChildren,
    ParentTicketSummary? Parent,
    IReadOnlyList<LinkedChildTicket> ChildTickets,
    TicketCompanyAlertSource? CompanyAlert,
    // v0.0.104 — the project this ticket is linked to (null when
    // unlinked) and, for project tickets, how many tickets link here.
    ProjectTicketSummary? Project = null,
    int ProjectLinkedTicketCount = 0);

/// v0.0.104 — summary of the project a ticket is linked to, for the
/// side panel + banner without a second round-trip.
public sealed class ProjectTicketSummary
{
    public Guid ProjectTicketId { get; set; }
    public long ProjectNumber { get; set; }
    public string ProjectSubject { get; set; } = string.Empty;
    public string? LinkedByName { get; set; }
}

/// The company row the on-open / on-create alert is rendered from.
public sealed class TicketCompanyAlertSource
{
    public Guid CompanyId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string AlertText { get; set; } = string.Empty;
    public bool AlertOnCreate { get; set; }
    public bool AlertOnOpen { get; set; }
    public string AlertOnOpenMode { get; set; } = string.Empty;
}

public enum LinkParentFailureReason
{
    SourceNotFound,
    ParentNotFound,
    SameTicket,
    ParentIsMerged,
    SourceIsMerged,
    WouldCycle,
}

public sealed record LinkParentResult(
    bool Success,
    LinkParentFailureReason? FailureReason);

public sealed class TicketPickerHit
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public string Subject { get; set; } = string.Empty;
    public Guid StatusId { get; set; }
    public string StatusName { get; set; } = string.Empty;
    public string StatusColor { get; set; } = string.Empty;
    public string StatusStateCategory { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public Guid RequesterContactId { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterFirstName { get; set; }
    public string? RequesterLastName { get; set; }
}

public enum MergeFailureReason
{
    SourceNotFound,
    TargetNotFound,
    SameTicket,
    AlreadyMerged,
    WouldCycle,
    CrossCustomerNotAcknowledged,
}

public sealed record MergeResult(
    bool Success,
    int MovedEventCount,
    long SourceNumber,
    long TargetNumber,
    bool CrossCustomer,
    MergeFailureReason? FailureReason);

public enum SplitFailureReason
{
    SourceNotFound,
    SourceMerged,
    SourceDeleted,
    MailEventNotFound,
    NotAMailEvent,
    DefaultsMissing,
}

public sealed record SplitResult(
    bool Success,
    Guid? NewTicketId,
    long? NewTicketNumber,
    long SourceNumber,
    SplitFailureReason? FailureReason);

public sealed class SplitChildTicket
{
    public Guid Id { get; set; }
    public long Number { get; set; }
}

public sealed record TicketDetail(
    Ticket Ticket,
    TicketBody Body,
    IReadOnlyList<TicketEvent> Events,
    IReadOnlyList<TicketEventPin> PinnedEvents);

public sealed record NewTicket(
    string Subject,
    string BodyText,
    string? BodyHtml,
    Guid RequesterContactId,
    Guid QueueId,
    Guid StatusId,
    Guid PriorityId,
    Guid? CategoryId,
    Guid? AssigneeUserId,
    string Source,
    Guid? CompanyId = null,
    bool AwaitingCompanyAssignment = false,
    string? CompanyResolvedVia = null,
    // v0.0.37 — set when the initial StatusId sits in the Pending
    // state-category. The endpoint computes the default value from
    // taxonomy + Tickets.PendingDefault* settings before calling
    // CreateAsync; the repository persists whatever it's given.
    DateTime? PendingTillUtc = null,
    // v0.0.39 — caller picks the ticket type explicitly when known
    // (e.g. when the LinkedTicketLauncher launched a manual trigger
    // for an "order" ticket). Null falls back to the 'support'
    // taxonomy row inside the repository so legacy callers stay
    // valid without each having to look up the support id.
    Guid? TicketTypeId = null,
    // v0.0.39 — optional first event written immediately after the
    // ticket is created. Used by the manual-trigger "create linked
    // ticket" flow to drop an opening note (internal or public)
    // into the freshly-created ticket's timeline. Null = no event,
    // identical to the pre-v0.0.39 behaviour.
    InitialTicketNote? InitialNote = null,
    // v0.0.104 — create the ticket as a project ticket (agent toggled
    // "Project ticket" in the new-ticket drawer). Default off.
    bool IsProject = false);

public sealed record InitialTicketNote(string BodyHtml, bool IsInternal);

public sealed record TicketFieldUpdate(
    Guid? QueueId = null,
    Guid? StatusId = null,
    Guid? PriorityId = null,
    Guid? CategoryId = null,
    Guid? AssigneeUserId = null,
    string? Subject = null,
    string? BodyText = null,
    string? BodyHtml = null,
    // v0.0.78 — explicit "unassign" signal. AssigneeUserId is a plain
    // Guid? where null means "field not provided" (same as every other
    // nullable here), so it cannot express "clear the assignee". When
    // this flag is true the repository sets assignee_user_id = NULL and
    // ignores AssigneeUserId. The two are mutually exclusive; the
    // endpoint never sets both.
    bool ClearAssignee = false,
    // v0.0.37 — when provided, sets the ticket's pending_till_utc.
    // Combined with the status-flip auto-clear in UpdateFieldsAsync
    // (status moves OUT of Pending → column is wiped regardless of
    // this value), the caller never needs an explicit "clear" flag for
    // the agent flow. Trigger-driven clears go through
    // `SetPendingTillHandler` / `SystemFieldMutator` instead.
    DateTime? PendingTillUtc = null,
    // v0.0.102 — set when the update is one leg of a bulk action. The
    // repository stamps `bulk_batch_id` into the metadata of every change
    // event it writes (StatusChange/QueueChange/…) so the timeline can
    // badge them and the audit trail can correlate the whole batch.
    Guid? BulkBatchId = null);

public sealed record NewTicketEvent(
    string EventType,
    string? BodyText,
    string? BodyHtml,
    bool IsInternal,
    Guid? AuthorUserId,
    Guid? AuthorContactId = null,
    string? MetadataJson = null);

public sealed record UpdateTicketEvent(
    string? BodyText,
    string? BodyHtml,
    bool? IsInternal,
    Guid EditorUserId);
