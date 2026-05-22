using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Dashboard;

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

    /// SignalR group that receives the per-agent AgentActivity broadcasts
    /// powering the dashboard tile. Admin-only — agents already see the
    /// per-ticket pill in the status bar; the cross-agent roll-up is for
    /// admins. The broadcaster owns the canonical constant; the hub
    /// references it for group enrollment on connect.
    private const string AgentActivityAdminsGroup =
        SignalRAgentActivityBroadcaster.AdminsGroup;

    private readonly IAgentActivityBroadcaster _agentActivityBroadcaster;

    public TicketPresenceHub(IAgentActivityBroadcaster agentActivityBroadcaster)
    {
        _agentActivityBroadcaster = agentActivityBroadcaster;
    }

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

        // Admins additionally join the agent-activity broadcast group so
        // the dashboard AgentActivity tile receives live per-agent updates.
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AgentActivityAdminsGroup);
        }

        // The newly-online agent's status changed (or this is a second tab
        // that just opened — still worth a refresh for admins to see).
        await BroadcastAgentActivity(state.UserId);

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

            // The disconnected agent may go fully offline (last tab) — push
            // an AgentActivity update so the dashboard tile flips them grey.
            await BroadcastAgentActivity(state.UserId);
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

        if (changed.Count > 0)
        {
            await BroadcastAgentActivity(state.UserId);
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

        if (previousTicketId != ticketId)
        {
            await BroadcastAgentActivity(state.UserId);
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
            await BroadcastAgentActivity(state.UserId);
        }
    }

    /// <summary>
    /// Client-invoked join for the timesheet-manager broadcast group
    /// (v0.0.35 commit H). The SPA calls this on mount when the
    /// <c>/auth/me</c> payload reports <c>timesheetManager: true</c>. We
    /// don't re-verify the flag here because the broadcast carries no
    /// secrets — it's only a "stale, refetch" ping; the actual data
    /// endpoints (<c>/api/timesheet/manager/*</c>) still enforce the
    /// manager flag server-side. SignalR cleans the group membership up
    /// automatically on disconnect.
    /// </summary>
    public Task JoinTimesheetManagers() =>
        Groups.AddToGroupAsync(Context.ConnectionId, "timesheet-managers");

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

    /// <summary>
    /// Returns the aggregated per-agent presence state across all live
    /// connections. Multiple tabs from the same agent collapse into one
    /// entry: viewing wins over recent. Online means at least one
    /// connection exists for that user. Exposed for the REST snapshot
    /// endpoint backing the dashboard AgentActivity tile.
    /// </summary>
    public static IReadOnlyDictionary<Guid, AgentPresenceState> GetAllAgentPresence()
    {
        var byUser = new Dictionary<Guid, AggregateBuilder>();
        foreach (var conn in Connections.Values)
        {
            if (!Guid.TryParse(conn.UserId, out var userGuid)) continue;
            if (!byUser.TryGetValue(userGuid, out var builder))
            {
                builder = new AggregateBuilder();
                byUser[userGuid] = builder;
            }
            if (conn.ViewingTicketId is not null) builder.Viewing ??= conn.ViewingTicketId;
            foreach (var id in conn.RecentTicketIds) builder.Recent.Add(id);
        }

        var result = new Dictionary<Guid, AgentPresenceState>(byUser.Count);
        foreach (var (id, builder) in byUser)
        {
            result[id] = new AgentPresenceState(builder.Viewing, builder.Recent, Online: true);
        }
        return result;
    }

    /// Aggregated presence for one user across their tabs. Public so the
    /// out-of-hub broadcaster (called from Telavox polling, etc.) can
    /// read it without reaching into the in-memory dictionary directly.
    public static AgentPresenceState GetAgentPresence(Guid userId)
        => GetAgentPresence(userId.ToString());

    private static AgentPresenceState GetAgentPresence(string userId)
    {
        string? viewing = null;
        var recent = new HashSet<string>();
        var online = false;
        foreach (var conn in Connections.Values)
        {
            if (!string.Equals(conn.UserId, userId, StringComparison.Ordinal)) continue;
            online = true;
            if (conn.ViewingTicketId is not null) viewing ??= conn.ViewingTicketId;
            foreach (var id in conn.RecentTicketIds) recent.Add(id);
        }
        return new AgentPresenceState(viewing, recent, online);
    }

    private async Task BroadcastTicketPresence(string ticketId)
    {
        var users = BuildPresenceForTicket(ticketId);
        await Clients.All.SendAsync("TicketPresence", ticketId, users);
    }

    private async Task BroadcastAgentActivity(string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid)) return;
        // CancellationToken.None — broadcasts must complete even when the
        // triggering connection has already been torn down (especially
        // from OnDisconnectedAsync, where ConnectionAborted is already
        // cancelled and would cancel the DB lookup mid-flight).
        await _agentActivityBroadcaster.BroadcastForUserAsync(userGuid, CancellationToken.None);
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

    private sealed class AggregateBuilder
    {
        public string? Viewing { get; set; }
        public HashSet<string> Recent { get; } = [];
    }
}

public sealed record TicketPresenceUser(string UserId, string Email, string Status);
