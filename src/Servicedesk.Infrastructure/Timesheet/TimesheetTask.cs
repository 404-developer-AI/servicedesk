namespace Servicedesk.Infrastructure.Timesheet;

/// One row in the admin-managed task catalogue (Settings → Timesheet tasks).
///
/// `RequiresTicket` — when true, an entry on this task must link a ticket
/// (e.g. Servicedesk, Project). When false, the agent registers time
/// without a ticket (e.g. Administratie, Verlof). The check lives in the
/// service layer; PostgreSQL can't express "ticket_id NOT NULL iff
/// task.requires_ticket = TRUE".
///
/// `IsAbsence` — entries on this task roll up separately in Tab 3 so a
/// verlof-dag doesn't look like a normal 8u-day. Independent of
/// `RequiresTicket` (e.g. Verlof is `!RequiresTicket && IsAbsence`,
/// Vergadering is `!RequiresTicket && !IsAbsence`).
///
/// `Archived` — soft-delete. Archived tasks stay queryable for legacy
/// entries but are filtered out of the picker in Tab 1.
public sealed class TimesheetTask
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool RequiresTicket { get; set; }
    public bool IsAbsence { get; set; }
    public bool Archived { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
