namespace Servicedesk.Infrastructure.Timesheet;

/// Enriched view returned to the UI. Includes joined task + ticket info so
/// the Tab-1 grid renders without extra round-trips (small N per day, but
/// avoids the N+1).
public sealed class TimesheetEntryRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// Email of the user that owns the row. Filled in for manager-scoped
    /// reads; for own-rows queries this is left empty by the SQL because
    /// the agent already knows it's their own data.
    public string UserEmail { get; set; } = "";
    public DateTime EntryDate { get; set; }
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
    public int Minutes { get; set; }
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = "";
    public bool TaskRequiresTicket { get; set; }
    public bool TaskIsAbsence { get; set; }
    public Guid? TicketId { get; set; }
    public long? TicketNumber { get; set; }
    public string? TicketSubject { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string Description { get; set; } = "";
    public bool Invoiced { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
