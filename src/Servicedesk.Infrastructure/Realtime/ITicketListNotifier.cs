namespace Servicedesk.Infrastructure.Realtime;

/// Fan-out for "something in the ticket list changed" events. The concrete
/// implementation lives in the Api project (SignalR hub) so Infrastructure
/// doesn't take a direct dependency on ASP.NET Core SignalR.
public interface ITicketListNotifier
{
    Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct);
}

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullTicketListNotifier : ITicketListNotifier
{
    public Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
}
