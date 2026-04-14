using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="ITicketListNotifier"/>. Sends
/// a "TicketListUpdated" ping to the <c>ticket-list</c> group so connected
/// clients invalidate their list query.
public sealed class SignalRTicketListNotifier : ITicketListNotifier
{
    private readonly IHubContext<TicketPresenceHub> _hub;

    public SignalRTicketListNotifier(IHubContext<TicketPresenceHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct)
        => _hub.Clients.Group("ticket-list")
               .SendAsync("TicketListUpdated", ticketId.ToString(), ct);
}
