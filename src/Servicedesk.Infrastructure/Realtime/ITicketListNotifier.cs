namespace Servicedesk.Infrastructure.Realtime;

/// Fan-out for "a ticket changed" events from background services (mail
/// ingest, SLA recalc, etc.) that don't sit on the ASP.NET Core SignalR
/// hub themselves. Implementations MUST broadcast both
/// <c>TicketListUpdated</c> (so the overview invalidates) and
/// <c>TicketUpdated</c> for the specific ticket (so an open detail page
/// re-fetches) — matching what the HTTP endpoints already send after a
/// mutation. A single notify call therefore covers every viewer surface.
public interface ITicketListNotifier
{
    Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct);
}

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullTicketListNotifier : ITicketListNotifier
{
    public Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
}
