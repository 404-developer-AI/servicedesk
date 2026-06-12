namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Pure state-machine that turns (prior baseline + current call + trigger
/// mode) into a (fire-popup yes/no + new baseline) decision. Extracted as a
/// stateless helper so the worker stays thin and the transition rules can be
/// unit-tested without spinning up Postgres / Telavox / SignalR.
///
/// Rules:
/// <list type="bullet">
/// <item><b>No active call</b> — baseline clears (both ids null). No fire.</item>
/// <item><b>New callId observed</b> — fire when the observed initial state
/// matches the trigger mode. The poll cadence is fast (default 2s) but a
/// restart-mid-call or a slow tick can still see the call already
/// answered, so the "answered"/"either" modes still fire even when we
/// missed the RINGING state for that call.</item>
/// <item><b>Same callId, state changed</b> — fire on the first edge that
/// matches the trigger mode. RINGING → ANSWERED transitions count as a
/// fresh fire even if a previous ringing edge already fired in mode
/// "either" (so the SPA can swap the popup from "incoming" to "live"
/// without doing the bookkeeping itself).</item>
/// <item><b>Same callId, same state</b> — no fire (debounce). Prevents a
/// long-running ANSWERED call from re-popping on every tick.</item>
/// </list>
public static class TelavoxCallTransition
{
    /// Result of evaluating one tick against the prior baseline. The
    /// <see cref="NewBaseline"/> always reflects what the next tick should
    /// compare against; the worker writes it to <c>telavox_call_state</c>
    /// regardless of whether the popup fired so a long-running call's
    /// state stays current without re-firing.
    public readonly record struct Decision(
        bool ShouldFire,
        TelavoxCallStateSnapshot NewBaseline);

    /// Inbound + outbound trigger modes recognised by the worker. Matches
    /// the values seeded for <see cref="Settings.SettingKeys.Telavox.PopupTriggerMode"/>.
    /// Unknown / malformed values fall through to the default
    /// (<see cref="Answered"/>) — fail-safe, no spam-fire on misconfig.
    public const string Answered = "answered";
    public const string Ringing = "ringing";
    public const string Either = "either";

    /// Computes whether to fire a popup right now.
    /// <paramref name="prior"/> is null when the agent has never been
    /// polled before (no row in <c>telavox_call_state</c>).
    /// <paramref name="current"/> is null when the agent's CAPI returned
    /// no active call this tick.
    public static Decision Evaluate(
        TelavoxCallStateSnapshot? prior,
        TelavoxCall? current,
        string triggerMode,
        Guid userId,
        DateTime nowUtc)
    {
        var mode = NormaliseMode(triggerMode);

        if (current is null)
        {
            // Idle: clear baseline (including the talk-time anchor). Retains
            // the row so the next flip to active is detected as a transition
            // rather than first-touch.
            return new Decision(
                ShouldFire: false,
                NewBaseline: new TelavoxCallStateSnapshot(userId, null, null, null, null, nowUtc));
        }

        var newCallId = current.CallId;
        var newState = current.State;

        // Talk-time anchor. Set it the first tick this call is observed
        // answered and hold it steady for the rest of the call (same callId,
        // already-anchored) so the completed-call row measures connected
        // time, not ring time. A call that is still only ringing has no
        // anchor yet. Held across worker restarts because `prior` comes from
        // the persisted row.
        DateTime? answeredAtUtc = null;
        if (IsAnswered(newState))
        {
            answeredAtUtc =
                string.Equals(prior?.LastCallId, newCallId, StringComparison.Ordinal)
                    ? prior?.AnsweredAtUtc ?? nowUtc
                    : nowUtc;
        }

        var newBaseline = new TelavoxCallStateSnapshot(
            userId, newCallId, newState, current.Direction, answeredAtUtc, nowUtc);

        // Popup is hard inbound-only: an agent's own outbound call never
        // fires, regardless of trigger mode. The baseline above is still
        // written for outbound calls so the dashboard call-state indicator
        // tracks them — only the SignalR popup is suppressed. An absent /
        // unknown direction is treated as inbound (its pre-direction
        // behaviour) so a CAPI vocab gap can't silence real inbound popups.
        if (TelavoxCallDirection.IsOutgoing(current.Direction))
        {
            return new Decision(false, newBaseline);
        }

        var priorCallId = prior?.LastCallId;
        var priorState = prior?.LastState;

        if (!string.Equals(priorCallId, newCallId, StringComparison.Ordinal))
        {
            // Fresh call (or first observation of this agent at all).
            // Fire when the observed initial state matches the mode.
            var fire = mode switch
            {
                Ringing  => IsRinging(newState),
                Answered => IsAnswered(newState),
                Either   => IsRinging(newState) || IsAnswered(newState),
                _ => false,
            };
            return new Decision(fire, newBaseline);
        }

        // Same call, possible state change.
        if (string.Equals(priorState, newState, StringComparison.Ordinal))
        {
            // Debounce: no edge, no fire.
            return new Decision(false, newBaseline);
        }

        // State edge.
        var fireEdge = mode switch
        {
            Answered => IsAnswered(newState) && !IsAnswered(priorState),
            Ringing  => IsRinging(newState) && !IsRinging(priorState),
            Either   => (IsAnswered(newState) && !IsAnswered(priorState))
                        || (IsRinging(newState) && !IsRinging(priorState)),
            _ => false,
        };
        return new Decision(fireEdge, newBaseline);
    }

    private static string NormaliseMode(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return trimmed switch
        {
            Ringing or Answered or Either => trimmed,
            _ => Answered, // fail-safe default
        };
    }

    /// Maps the assorted state strings the CAPI <c>OngoingCallDto</c>
    /// vocabulary uses (verified empirically against a live tenant:
    /// <c>"ringing"</c>, <c>"up"</c>, <c>"down"</c>, <c>"dialing"</c>,
    /// <c>"offhook"</c>, …) plus the uppercase forms my earlier draft
    /// assumed. Matching is case-insensitive so a future CAPI vocab tweak
    /// or a vendor variant doesn't silently break the popup.
    private static bool IsRinging(string? state) =>
        EqualsAny(state, "ringing", "ring", "alerting");

    private static bool IsAnswered(string? state) =>
        EqualsAny(state, "up", "answered", "connected", "active");

    private static bool EqualsAny(string? value, params string[] candidates)
    {
        if (value is null) return false;
        foreach (var c in candidates)
        {
            if (string.Equals(value, c, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
