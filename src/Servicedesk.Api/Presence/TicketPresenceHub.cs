using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Servicedesk.Api.Presence;

/// <summary>
/// Tracks which agents are viewing or have recently opened tickets.
/// Broadcasts presence updates so the sidebar can show colored (viewing)
/// or greyed-out (recent) avatars per ticket.
/// </summary>
[Authorize(Policy = "RequireAgent")]
public sealed class TicketPresenceHub : Hub
{
    // In-memory state — fine for single-server deployments.
    // Key: connectionId → connection state.
    private static readonly ConcurrentDictionary<string, ConnectionState> Connections = new();

    public override async Task OnConnectedAsync()
    {
        var state = new ConnectionState
        {
            UserId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            Email = Context.User?.FindFirstValue(ClaimTypes.Email) ?? "",
        };
        Connections[Context.ConnectionId] = state;

        // Every connected client joins the ticket-list group so they
        // receive lightweight "something changed" pings for the list view.
        await Groups.AddToGroupAsync(Context.ConnectionId, "ticket-list");

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Connections.TryRemove(Context.ConnectionId, out var state))
        {
            // Broadcast removal for any ticket this connection was viewing or had recent
            var affectedTicketIds = new HashSet<string>(state.RecentTicketIds);
            if (state.ViewingTicketId is not null)
                affectedTicketIds.Add(state.ViewingTicketId);

            foreach (var ticketId in affectedTicketIds)
            {
                await BroadcastTicketPresence(ticketId);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client sends their current recent ticket IDs so other users
    /// can see greyed-out avatars for those tickets.
    /// </summary>
    public async Task SyncRecent(string[] ticketIds)
    {
        if (!Connections.TryGetValue(Context.ConnectionId, out var state)) return;

        var oldIds = new HashSet<string>(state.RecentTicketIds);
        state.RecentTicketIds = ticketIds.Take(10).ToHashSet();
        var newIds = state.RecentTicketIds;

        // Broadcast for tickets that were added or removed
        var changed = new HashSet<string>(oldIds);
        changed.SymmetricExceptWith(newIds);

        foreach (var ticketId in changed)
        {
            await BroadcastTicketPresence(ticketId);
        }
    }

    /// <summary>
    /// Client calls when opening a ticket detail page.
    /// </summary>
    public async Task StartViewing(string ticketId)
    {
        if (!Connections.TryGetValue(Context.ConnectionId, out var state)) return;

        var previousTicketId = state.ViewingTicketId;
        state.ViewingTicketId = ticketId;

        // Join the SignalR group for this ticket so we receive live updates
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket:{ticketId}");

        // Leave the previous ticket's group if switching
        if (previousTicketId is not null && previousTicketId != ticketId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket:{previousTicketId}");
        }

        // Broadcast presence update for the newly viewed ticket
        await BroadcastTicketPresence(ticketId);

        // If they were viewing a different ticket before, update that one too
        if (previousTicketId is not null && previousTicketId != ticketId)
        {
            await BroadcastTicketPresence(previousTicketId);
        }
    }

    /// <summary>
    /// Client calls when leaving a ticket detail page.
    /// </summary>
    public async Task StopViewing()
    {
        if (!Connections.TryGetValue(Context.ConnectionId, out var state)) return;

        var previousTicketId = state.ViewingTicketId;
        state.ViewingTicketId = null;

        if (previousTicketId is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket:{previousTicketId}");
            await BroadcastTicketPresence(previousTicketId);
        }
    }

    /// <summary>
    /// Client can request the full presence snapshot (e.g. on reconnect).
    /// Returns presence for all tickets that any connection is viewing or has recent.
    /// </summary>
    public async Task RequestFullSync()
    {
        var allTicketIds = new HashSet<string>();
        foreach (var conn in Connections.Values)
        {
            if (conn.ViewingTicketId is not null) allTicketIds.Add(conn.ViewingTicketId);
            foreach (var id in conn.RecentTicketIds) allTicketIds.Add(id);
        }

        var snapshot = new Dictionary<string, List<TicketPresenceUser>>();
        foreach (var ticketId in allTicketIds)
        {
            var users = BuildPresenceForTicket(ticketId);
            if (users.Count > 0) snapshot[ticketId] = users;
        }

        await Clients.Caller.SendAsync("FullSync", snapshot);
    }

    private async Task BroadcastTicketPresence(string ticketId)
    {
        var users = BuildPresenceForTicket(ticketId);
        await Clients.All.SendAsync("TicketPresence", ticketId, users);
    }

    private List<TicketPresenceUser> BuildPresenceForTicket(string ticketId)
    {
        // Aggregate across all connections — a user may have multiple tabs.
        // If ANY connection for that user is viewing the ticket → "viewing".
        // Otherwise if ANY connection has it in recent → "recent".
        var userMap = new Dictionary<string, TicketPresenceUser>();

        foreach (var conn in Connections.Values)
        {
            var isViewing = conn.ViewingTicketId == ticketId;
            var isRecent = conn.RecentTicketIds.Contains(ticketId);

            if (!isViewing && !isRecent) continue;

            if (userMap.TryGetValue(conn.UserId, out var existing))
            {
                // Upgrade to viewing if any tab is viewing
                if (isViewing && existing.Status == "recent")
                {
                    userMap[conn.UserId] = existing with { Status = "viewing" };
                }
            }
            else
            {
                userMap[conn.UserId] = new TicketPresenceUser(
                    conn.UserId,
                    conn.Email,
                    isViewing ? "viewing" : "recent");
            }
        }

        return userMap.Values.ToList();
    }

    private sealed class ConnectionState
    {
        public required string UserId { get; init; }
        public required string Email { get; init; }
        public string? ViewingTicketId { get; set; }
        public HashSet<string> RecentTicketIds { get; set; } = [];
    }
}

public sealed record TicketPresenceUser(string UserId, string Email, string Status);
