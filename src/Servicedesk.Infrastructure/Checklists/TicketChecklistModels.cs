namespace Servicedesk.Infrastructure.Checklists;

/// Item states. Stored as lower-case strings (CHECK constraint on the table).
public static class ChecklistItemState
{
    public const string Open = "open";
    public const string Done = "done";
    public const string NotApplicable = "na";

    public static bool IsValid(string? s) => s is Open or Done or NotApplicable;
}

/// Kinds in the per-item log.
public static class ChecklistItemEventKind
{
    public const string StateChange = "state_change";
    public const string Comment = "comment";
    public const string ItemAdded = "item_added";
    public const string ItemEdited = "item_edited";
    public const string ItemRemoved = "item_removed";
}

/// One attached checklist (row of <c>ticket_checklists</c>) with its
/// maintained counters. Plain mutable class so Dapper maps it by name.
public sealed class TicketChecklistRow
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid? TemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool BlockClose { get; set; }
    public int SortOrder { get; set; }
    public Guid? AttachedByUserId { get; set; }
    public string? AttachedByName { get; set; }
    public DateTime AttachedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int RequiredTotal { get; set; }
    public int RequiredDone { get; set; }
    public int TotalItems { get; set; }
    public int DoneItems { get; set; }
    public bool Touched { get; set; }
}

public sealed class TicketChecklistSection
{
    public Guid Id { get; set; }
    public Guid ChecklistId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class TicketChecklistItem
{
    public Guid Id { get; set; }
    public Guid ChecklistId { get; set; }
    public Guid TicketId { get; set; }
    public Guid? SectionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TeamLabel { get; set; } = string.Empty;
    public string TimingLabel { get; set; } = string.Empty;
    public string LinkUrl { get; set; } = string.Empty;
    public string LinkLabel { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public bool IsAdHoc { get; set; }
    public Guid? AddedByUserId { get; set; }
    public string? AddedByName { get; set; }
    public string State { get; set; } = ChecklistItemState.Open;
    public DateTime? StateChangedUtc { get; set; }
    public Guid? StateChangedByUserId { get; set; }
    public string? StateChangedByName { get; set; }
    public string NaReason { get; set; } = string.Empty;
    public int CommentCount { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class TicketChecklistItemEvent
{
    public long Id { get; set; }
    public Guid ItemId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed record TicketChecklistView(
    TicketChecklistRow Checklist,
    IReadOnlyList<TicketChecklistSection> Sections,
    IReadOnlyList<TicketChecklistItem> Items);

/// Outcome of a state change: whether anything changed and the checklist's
/// completion before/after so the service can log completed / reopened.
public sealed record ChecklistItemStateChange(
    bool Changed,
    Guid ChecklistId,
    Guid TicketId,
    string ChecklistName,
    string FromState,
    string ToState,
    bool WasComplete,
    bool IsComplete);

/// One checklist that blocks a closing status change: id + name + how many
/// required items are still open. Surfaced in the 409 body and the bulk
/// skip reason detail.
public sealed record ChecklistBlocker(Guid ChecklistId, string Name, int OpenRequired);
