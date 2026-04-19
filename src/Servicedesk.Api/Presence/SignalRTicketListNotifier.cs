using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="ITicketListNotifier"/>.
/// Broadcasts both "TicketListUpdated" (to the <c>ticket-list</c> group so
/// the overview invalidates) and "TicketUpdated" (to the ticket's own
/// <c>ticket:{id}</c> group so an open detail page re-fetches). This matches
/// what the HTTP endpoints in <c>TicketEndpoints</c> already fan out — so a
/// mutation via the mail-ingest background service refreshes viewers the
/// same way an HTTP-triggered mutation does.
public sealed class SignalRTicketListNotifier : ITicketListNotifier
{
    private readonly IHubContext<TicketPresenceHub> _hub;

    public SignalRTicketListNotifier(IHubContext<TicketPresenceHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct)
    {
        var id = ticketId.ToString();
        return Task.WhenAll(
            _hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", id, ct),
            _hub.Clients.Group($"ticket:{id}").SendAsync("TicketUpdated", id, ct));
    }
}
