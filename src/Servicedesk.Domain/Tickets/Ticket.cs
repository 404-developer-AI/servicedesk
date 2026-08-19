namespace Servicedesk.Domain.Tickets;

public sealed record Ticket(
    Guid Id,
    long Number,
    string Subject,
    Guid RequesterContactId,
    Guid? AssigneeUserId,
    Guid QueueId,
    Guid StatusId,
    Guid PriorityId,
    Guid? CategoryId,
    string Source,
    string? ExternalRef,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? DueUtc,
    DateTime? FirstResponseUtc,
    DateTime? ResolvedUtc,
    DateTime? ClosedUtc,
    bool IsDeleted,
    Guid? CompanyId = null,
    bool AwaitingCompanyAssignment = false,
    string? CompanyResolvedVia = null,
    Guid? MergedIntoTicketId = null,
    DateTime? MergedUtc = null,
    Guid? MergedByUserId = null,
    Guid? SplitFromTicketId = null,
    DateTime? SplitFromUtc = null,
    Guid? SplitFromUserId = null,
    // v0.0.37 — pending_till_utc + pending_till_next_trigger_id were
    // added to the schema in v0.0.24 for trigger-driven reminder cycles
    // (see `SetPendingTillHandler`). The agent-facing fields are
    // surfaced through the domain model from v0.0.37 onwards so the
    // side panel + new-ticket drawer can read/write a "Pending till"
    // datetime when the status sits in the Pending state-category.
    DateTime? PendingTillUtc = null,
    Guid? PendingTillNextTriggerId = null,
    Guid? ParentTicketId = null,
    DateTime? ParentLinkedUtc = null,
    Guid? ParentLinkedByUserId = null,
    // v0.0.39 — first-class ticket type. NOT NULL at the database
    // layer (backfilled to 'support' for pre-existing rows); declared
    // as a required Guid here so Dapper hydration cannot leave it
    // default-zeroed silently. New positional position is appended at
    // the end so older positional callers (none observed in-repo)
    // would surface as compile errors.
    Guid TicketTypeId = default,
    // v0.0.41 phase 5 — Zammad migration provenance. Both nullable;
    // populated only on tickets created by the Zammad import. The
    // detail page renders an "Imported from Zammad #N" badge in the
    // side panel when these are non-null. Surface as nullable string
    // / nullable long so a JSON-deserialised Ticket from an older
    // payload still hydrates cleanly.
    long? ZammadTicketId = null,
    string? ZammadTicketNumber = null,
    // v0.0.105 — project tickets. IsProject flags this ticket as a
    // project; ProjectTicketId links a normal ticket to (at most) one
    // project ticket. ProjectSortOrder is the manual priority position
    // inside the project's panel. ProjectPromptDismissedUtc remembers
    // that the link-to-project prompt was answered (linked or declined)
    // so it never re-asks on this ticket.
    bool IsProject = false,
    Guid? ProjectTicketId = null,
    DateTime? ProjectLinkedUtc = null,
    Guid? ProjectLinkedByUserId = null,
    int ProjectSortOrder = 0,
    DateTime? ProjectPromptDismissedUtc = null);

public sealed record TicketBody(
    Guid TicketId,
    string BodyText,
    string? BodyHtml);

public sealed record TicketEvent(
    long Id,
    Guid TicketId,
    string EventType,
    Guid? AuthorUserId,
    Guid? AuthorContactId,
    string? AuthorName,
    string? BodyText,
    string? BodyHtml,
    string MetadataJson,
    bool IsInternal,
    DateTime CreatedUtc,
    DateTime? EditedUtc,
    Guid? EditedByUserId);

public sealed record TicketEventRevision(
    long Id,
    long EventId,
    int RevisionNumber,
    string? BodyTextBefore,
    string? BodyHtmlBefore,
    bool IsInternalBefore,
    Guid EditedByUserId,
    string? EditedByName,
    DateTime EditedUtc);

public sealed record TicketEventPin(
    long Id,
    long EventId,
    Guid TicketId,
    Guid PinnedByUserId,
    string? PinnedByName,
    string Remark,
    DateTime CreatedUtc);
