namespace Servicedesk.Infrastructure.Dashboard;

/// Enriched per-agent activity snapshot consumed by the dashboard
/// AgentActivity tile. Combines the agent's identity (resolved from the
/// users table) with whatever tickets they are currently viewing or
/// have in their recent list (resolved from the presence hub).
public sealed record AgentActivity(
    Guid UserId,
    string Email,
    string RoleName,
    bool Online,
    /// "idle" (no call or unmapped), "ringing" (phone ringing), or
    /// "answered" (agent picked up). Resolved from
    /// <c>telavox_call_state.last_state</c> for users with a Telavox link;
    /// always "idle" for unlinked users.
    string CallState,
    AgentActivityTicket? Viewing,
    IReadOnlyList<AgentActivityTicket> Recent);

/// Ticket-summary shape carried inside <see cref="AgentActivity"/>. Kept
/// deliberately small so a frequent broadcast doesn't ship the full
/// ticket detail per agent change.
public sealed record AgentActivityTicket(
    Guid Id,
    long Number,
    string Subject,
    string StatusName,
    string StatusColor,
    string StatusStateCategory);

/// Pure presence state per agent, as known by the SignalR hub. The
/// service combines this with the users table to produce
/// <see cref="AgentActivity"/>. Owned by the API project; the
/// infrastructure service consumes it as a value object.
public sealed record AgentPresenceState(
    string? ViewingTicketId,
    IReadOnlyCollection<string> RecentTicketIds,
    bool Online);

/// Categorisation helper for raw Telavox call-state strings. Same source
/// of truth as <see cref="AgentActivityService"/> uses to populate the
/// snapshot — keep in sync with the polling worker's edge detection so
/// the live broadcast and the snapshot agree on when to flip the
/// indicator on the dashboard tile.
public static class AgentCallStateCategorization
{
    public const string Idle = "idle";
    public const string Ringing = "ringing";
    public const string Answered = "answered";

    private static readonly HashSet<string> RingingStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "ringing", "ring", "alerting",
    };
    private static readonly HashSet<string> AnsweredStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "answered", "connected", "active",
    };

    public static string Categorize(string? rawState)
    {
        if (string.IsNullOrWhiteSpace(rawState)) return Idle;
        if (RingingStates.Contains(rawState)) return Ringing;
        if (AnsweredStates.Contains(rawState)) return Answered;
        return Idle;
    }
}

public interface IAgentActivityService
{
    /// Returns the dashboard tile snapshot: every Agent/Admin in the
    /// system, with online flag and tickets resolved from the supplied
    /// presence map. Offline agents are included but carry an empty
    /// ticket payload — the tile greys them out in the left column.
    Task<IReadOnlyList<AgentActivity>> BuildSnapshotAsync(
        IReadOnlyDictionary<Guid, AgentPresenceState> presence,
        CancellationToken ct);

    /// Builds the activity record for a single agent — used by the hub
    /// to broadcast incremental updates when one agent's presence
    /// changes. Returns null if the user isn't an Agent/Admin (shouldn't
    /// happen given the hub policy, but defensive).
    Task<AgentActivity?> BuildForUserAsync(
        Guid userId,
        AgentPresenceState presence,
        CancellationToken ct);
}

/// Pushes a fresh <see cref="AgentActivity"/> for a single user to all
/// connected admin clients. Used by both the SignalR presence hub
/// (presence transitions) and the Telavox polling worker (call-state
/// transitions) so the dashboard tile stays in sync regardless of
/// which subsystem caused the change. The implementation reads the
/// in-process presence state owned by the API project and builds the
/// activity record via <see cref="IAgentActivityService"/>.
public interface IAgentActivityBroadcaster
{
    Task BroadcastForUserAsync(Guid userId, CancellationToken ct);
}
