namespace Servicedesk.Infrastructure.Realtime;

/// v0.0.35 commit H — fan-out for "a timesheet entry was created / updated /
/// deleted." Two broadcasts in one call:
///
///   1. Manager fan-out → all connected timesheet-managers receive
///      <c>TimesheetEntriesChanged</c> so Tab 2 / Tab 3 invalidate their
///      queries.
///   2. Ticket-detail fan-out → viewers of the affected ticket receive
///      <c>TicketTimesheetUpdated</c> so the expansion panel on the ticket
///      page re-fetches.
///
/// Payloads are deliberately minimal — just a "stale, refetch" ping. The
/// SPA fetches the full row-set from the existing GET endpoints, which
/// re-runs row-level authorization. No entry data crosses the push channel.
/// </summary>
public interface ITimesheetEntryNotifier
{
    /// Broadcast both the manager-fan-out and (when <paramref name="ticketId"/>
    /// is provided) the ticket-detail fan-out. Pass null for ticketId when
    /// the entry has no ticket attached (Verlof, Administratie, …).
    Task NotifyEntryChangedAsync(Guid? ticketId, CancellationToken ct);
}

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullTimesheetEntryNotifier : ITimesheetEntryNotifier
{
    public Task NotifyEntryChangedAsync(Guid? ticketId, CancellationToken ct)
        => Task.CompletedTask;
}
