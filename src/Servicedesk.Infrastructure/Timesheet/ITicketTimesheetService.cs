namespace Servicedesk.Infrastructure.Timesheet;

/// Ticket-scoped reads for the v0.0.35-F integration on the ticket detail
/// page. Returns every timesheet entry linked to a given ticket (across
/// all agents) so the expandable panel can show "who worked how long".
///
/// Authorization: any agent/admin that can see the ticket can see the
/// entries. Customers never reach these endpoints (no customer route
/// surface exists in v0.0.35). Manager / non-manager makes no difference
/// here — the data is part of the ticket's internal trail.
public interface ITicketTimesheetService
{
    /// All entries linked to <paramref name="ticketId"/>, joined with task
    /// and owning-user email. Ordered newest-day-first and start-asc within
    /// a day so the panel reads like a chronological log.
    Task<IReadOnlyList<TimesheetEntryRow>> ListByTicketAsync(
        Guid ticketId,
        CancellationToken ct = default);

    /// Pre-rendered HTML suitable for pasting into the reply editor. Uses
    /// the admin-configurable header/row/footer template fragments stored
    /// in Settings under <c>Timesheet.Reply*</c>. Row data is HTML-escaped
    /// before substitution so a malicious description cannot break out of
    /// the template.
    Task<string> BuildReplyHtmlAsync(
        Guid ticketId,
        CancellationToken ct = default);
}
