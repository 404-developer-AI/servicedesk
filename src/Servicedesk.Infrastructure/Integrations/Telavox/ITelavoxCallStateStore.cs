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
    /// transition rather than first-touch. <paramref name="lastDirection"/>
    /// is the call's direction; persisted so the "call completed" activity
    /// row can record it on the answered→idle edge, where the live call has
    /// already gone. <paramref name="answeredAtUtc"/> is when the current
    /// call first reached the answered state — held steady across ticks for
    /// the same call so the completed-call row can report talk-time; null
    /// while the call is only ringing, and at idle.
    Task UpsertAsync(
        Guid userId,
        string? lastCallId,
        string? lastState,
        string? lastDirection,
        DateTime? answeredAtUtc,
        DateTime lastSeenUtc,
        CancellationToken ct = default);
}

/// Snapshot of <c>telavox_call_state</c> for one agent. <see cref="LastCallId"/>,
/// <see cref="LastState"/> and <see cref="LastDirection"/> are all null when
/// the agent has been seen at least once but is currently idle.
/// <see cref="AnsweredAtUtc"/> is the talk-time anchor: the moment the active
/// call first reached the answered state, or null when no answered call is in
/// progress.
public sealed record TelavoxCallStateSnapshot(
    Guid UserId,
    string? LastCallId,
    string? LastState,
    string? LastDirection,
    DateTime? AnsweredAtUtc,
    DateTime LastSeenUtc);
