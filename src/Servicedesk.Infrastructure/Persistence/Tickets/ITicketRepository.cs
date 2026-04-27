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

    /// Lightweight ticket numbers + subjects for the merge picker autocomplete.
    /// Filters out merged/deleted tickets and the source ticket itself; queue
    /// access is enforced by the caller passing AccessibleQueueIds (null = admin).
    Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(
        string? search,
        Guid excludeTicketId,
        IReadOnlyCollection<Guid>? accessibleQueueIds,
        int limit,
        CancellationToken ct);

    /// Returns the ticket numbers that have been merged INTO this ticket so the
    /// detail view can render the "Merged from #1234, #5678" strip without a
    /// second round-trip.
    Task<IReadOnlyList<long>> GetMergedSourceTicketNumbersAsync(Guid targetTicketId, CancellationToken ct);

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

    /// Returns the (id, number) pairs of tickets that were split off from this
    /// ticket so the detail view can render a clickable "Split into #1234,
    /// #5678" strip without a second round-trip.
    Task<IReadOnlyList<SplitChildTicket>> GetSplitChildrenAsync(Guid parentTicketId, CancellationToken ct);

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
}

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
    string? CompanyResolvedVia = null);

public sealed record TicketFieldUpdate(
    Guid? QueueId = null,
    Guid? StatusId = null,
    Guid? PriorityId = null,
    Guid? CategoryId = null,
    Guid? AssigneeUserId = null,
    string? Subject = null,
    string? BodyText = null,
    string? BodyHtml = null);

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
