using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="ITimesheetEntryNotifier"/>.
/// Reuses <see cref="TicketPresenceHub"/> for both fan-outs so we don't
/// stand up a second hub for one feature:
///   - <c>timesheet-managers</c> group → "TimesheetEntriesChanged" (Tab 2 / Tab 3 ping)
///   - <c>ticket:{id}</c> group        → "TicketTimesheetUpdated"  (ticket-detail panel ping)
public sealed class SignalRTimesheetEntryNotifier : ITimesheetEntryNotifier
{
    private readonly IHubContext<TicketPresenceHub> _hub;

    public SignalRTimesheetEntryNotifier(IHubContext<TicketPresenceHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyEntryChangedAsync(Guid? ticketId, CancellationToken ct)
    {
        // Both broadcasts are independent; fire-and-await in parallel so a
        // slow client on one group doesn't block the other group's push.
        var managerBroadcast = _hub.Clients
            .Group("timesheet-managers")
            .SendAsync("TimesheetEntriesChanged", ct);

        if (ticketId is null || ticketId == Guid.Empty)
            return managerBroadcast;

        var ticketBroadcast = _hub.Clients
            .Group($"ticket:{ticketId}")
            .SendAsync("TicketTimesheetUpdated", ticketId.Value.ToString(), ct);

        return Task.WhenAll(managerBroadcast, ticketBroadcast);
    }
}
