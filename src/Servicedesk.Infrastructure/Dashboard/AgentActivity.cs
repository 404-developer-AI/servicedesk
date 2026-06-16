using System.Text.Json.Serialization;
using Servicedesk.Infrastructure.Access;

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
///
/// <see cref="QueueId"/> is server-only (never serialized) — it drives
/// the per-recipient masking in <see cref="AgentActivityMasking"/> so a
/// viewer without access to the ticket's queue receives a
/// <see cref="Restricted"/> placeholder with no subject or number.
public sealed record AgentActivityTicket(
    Guid Id,
    long Number,
    string Subject,
    string StatusName,
    string StatusColor,
    string StatusStateCategory,
    bool Restricted = false,
    [property: JsonIgnore] Guid QueueId = default);

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

/// Masks ticket references in an <see cref="AgentActivity"/> for a
/// specific viewer. The service builds the record once with full ticket
/// data (carrying <see cref="AgentActivityTicket.QueueId"/>); this helper
/// then produces the per-viewer projection so the REST snapshot and the
/// SignalR broadcast both enforce the same queue-level authorization the
/// ticket-detail endpoint applies. A ticket in a queue the viewer cannot
/// access becomes a <see cref="AgentActivityTicket.Restricted"/>
/// placeholder: no subject, no number, no status — the row stays so the
/// viewer still sees that the agent is busy, without learning what with.
public static class AgentActivityMasking
{
    public static AgentActivity ForViewer(AgentActivity full, QueueAccessScope viewer)
    {
        // Admins (and any viewer who can see every referenced queue) get
        // the record unchanged — avoids allocating copies on the hot path.
        if (viewer.IsAdmin) return full;

        var viewing = full.Viewing is null ? null : MaskTicket(full.Viewing, viewer);
        var recent = full.Recent;
        List<AgentActivityTicket>? maskedRecent = null;
        for (var i = 0; i < recent.Count; i++)
        {
            var masked = MaskTicket(recent[i], viewer);
            if (!ReferenceEquals(masked, recent[i]))
            {
                maskedRecent ??= new List<AgentActivityTicket>(recent);
                maskedRecent[i] = masked;
            }
        }

        if (ReferenceEquals(viewing, full.Viewing) && maskedRecent is null)
            return full;

        return full with { Viewing = viewing, Recent = maskedRecent ?? recent };
    }

    private static AgentActivityTicket MaskTicket(AgentActivityTicket t, QueueAccessScope viewer)
    {
        if (viewer.CanSee(t.QueueId)) return t;
        // Keep Id so React can key the row; drop everything else and flag
        // it restricted. Number 0 / empty strings are never rendered once
        // the client sees Restricted = true.
        return new AgentActivityTicket(
            Id: t.Id,
            Number: 0,
            Subject: "",
            StatusName: "",
            StatusColor: "",
            StatusStateCategory: "",
            Restricted: true,
            QueueId: default);
    }
}
