namespace Servicedesk.Infrastructure.Feedback;

/// One row of the Employee Feedback board ("Aandachtspunten & Successen"),
/// enriched with the joined employee, work-point type, completed-by user, and
/// linked-ticket display fields so the grid renders without an N+1.
///
/// Sealed class (get/set) rather than a record so Dapper binds by column
/// alias, matching the convention used by <c>TimesheetEntryRow</c>.
public sealed class FeedbackEntryRow
{
    public Guid Id { get; set; }
    public Guid TargetUserId { get; set; }
    public string TargetUserEmail { get; set; } = "";
    public DateTime EntryDate { get; set; }
    public string BodyHtml { get; set; } = "";
    public string ManagementRemarksHtml { get; set; } = "";
    public Guid? WorkPointTypeId { get; set; }
    public string? WorkPointTypeName { get; set; }
    public string? WorkPointTypeColor { get; set; }
    public bool IsCompleted { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public string? CompletedByEmail { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public Guid? LinkedTicketId { get; set; }
    public long? LinkedTicketNumber { get; set; }
    public string? LinkedTicketSubject { get; set; }
    /// When set, the entry was logged from this specific ticket-timeline event;
    /// the board deep-links the ticket cell to it (/tickets/{id}#event-{id}).
    public long? LinkedTicketEventId { get; set; }
    /// 'manual' (typed on the board) or 'activity' (logged from a timeline item).
    public string Source { get; set; } = "manual";
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// Full-row update payload. Every editable column is written in one shot
/// (the row leaves edit mode atomically). <see cref="LinkedTicketNumber"/> is
/// the human ticket number the agent typed; the service resolves it to a
/// ticket id server-side (not found → stored as a non-clickable number).
public sealed record FeedbackEntryInput(
    Guid TargetUserId,
    DateOnly EntryDate,
    string BodyHtml,
    string ManagementRemarksHtml,
    Guid? WorkPointTypeId,
    bool IsCompleted,
    long? LinkedTicketNumber);

/// Field-level validation error so the UI can highlight the exact input.
/// <c>Field</c> matches the JSON property the client sent.
public sealed record FeedbackFieldError(string Field, string Message);

/// Payload for logging a feedback entry directly from a ticket-timeline item
/// (the activity "Log feedback" button). Unlike the board's blank-draft create,
/// this creates a fully-populated row in one shot, stamped source='activity',
/// linked to the originating ticket + the exact timeline event.
public sealed record LogFeedbackInput(
    Guid TargetUserId,
    DateOnly EntryDate,
    Guid? WorkPointTypeId,
    string BodyHtml,
    Guid LinkedTicketId,
    long LinkedTicketNumber,
    long LinkedTicketEventId);

public abstract record CreateFeedbackEntryResult
{
    public sealed record Created(FeedbackEntryRow Row) : CreateFeedbackEntryResult;
    public sealed record ValidationFailed(IReadOnlyList<FeedbackFieldError> Errors) : CreateFeedbackEntryResult;
}

public abstract record UpdateFeedbackEntryResult
{
    public sealed record Updated(FeedbackEntryRow Row) : UpdateFeedbackEntryResult;
    public sealed record NotFound : UpdateFeedbackEntryResult;
    public sealed record ValidationFailed(IReadOnlyList<FeedbackFieldError> Errors) : UpdateFeedbackEntryResult;
}

/// Filters for the board list. All optional; null = no restriction.
public sealed record FeedbackEntryFilter(
    Guid? TargetUserId,
    Guid? WorkPointTypeId,
    bool? IsCompleted);

/// Result of resolving a typed ticket number to a ticket. <see cref="Id"/>
/// is null when no live ticket carries that number.
public sealed record FeedbackTicketResolution(long Number, Guid? Id, string? Subject);

/// One selectable employee for the board's "employee" dropdown — active
/// Agent/Admin accounts only.
public sealed record FeedbackEmployee(Guid Id, string Email);

/// Tells the ticket timeline which events already have feedback logged against
/// them (so the "Log feedback" button can mark them and avoid double logging).
/// <see cref="Loggers"/> is the distinct set of creator emails.
public sealed class FeedbackLoggedEvent
{
    public long EventId { get; set; }
    public int Count { get; set; }
    public string[] Loggers { get; set; } = Array.Empty<string>();
}

/// Admin-managed work-point type (e.g. "Procedure not followed", "Order &
/// punctuality"). Soft-deleted via <see cref="IsActive"/>.
public sealed class FeedbackWorkPointType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7c7cff";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public abstract record CreateWorkPointTypeResult
{
    public sealed record Created(FeedbackWorkPointType Type) : CreateWorkPointTypeResult;
    public sealed record NameConflict : CreateWorkPointTypeResult;
}

public abstract record UpdateWorkPointTypeResult
{
    public sealed record Updated(FeedbackWorkPointType Type) : UpdateWorkPointTypeResult;
    public sealed record NotFound : UpdateWorkPointTypeResult;
    public sealed record NameConflict : UpdateWorkPointTypeResult;
}
