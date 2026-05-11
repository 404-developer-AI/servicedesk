namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Per-agent transition baseline. The polling worker reads this on every
/// tick to decide whether the current call represents a fresh event (new
/// callId, or a state-transition such as RINGING → ANSWERED) and so
/// whether the SignalR push should fire. The row's primary-key is
/// <c>user_id</c> so the store is keyed in lockstep with
/// <see cref="ITelavoxAgentLinkStore"/>; the FK to <c>users</c> is
/// <c>ON DELETE CASCADE</c> so a hard user-delete drops both rows.
public interface ITelavoxCallStateStore
{
    Task<TelavoxCallStateSnapshot?> GetAsync(Guid userId, CancellationToken ct = default);

    /// Upserts the per-agent baseline. <paramref name="lastCallId"/> /
    /// <paramref name="lastState"/> may be null when the current poll
    /// returned no active call — that is the "idle" baseline. The row is
    /// retained even at idle so a future flip to RINGING is detected as a
    /// transition rather than first-touch.
    Task UpsertAsync(
        Guid userId,
        string? lastCallId,
        string? lastState,
        DateTime lastSeenUtc,
        CancellationToken ct = default);
}

/// Snapshot of <c>telavox_call_state</c> for one agent. Both
/// <see cref="LastCallId"/> and <see cref="LastState"/> may be null when
/// the agent has been seen at least once but is currently idle.
public sealed record TelavoxCallStateSnapshot(
    Guid UserId,
    string? LastCallId,
    string? LastState,
    DateTime LastSeenUtc);
