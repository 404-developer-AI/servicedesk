using Servicedesk.Infrastructure.Integrations.Telavox;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.34 commit C — pins the state-machine inside
/// <see cref="TelavoxCallTransition"/>. The worker is otherwise a thin
/// shell around this function plus Telavox HTTP + SignalR, so these tests
/// are the load-bearing guard for "does the popup fire correctly".
public sealed class TelavoxCallTransitionTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Now = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    private static TelavoxCall MakeCall(
        string id, string state, string direction = TelavoxCallDirection.Incoming) =>
        new(id, state, FromNumber: "+3245555555", ToNumber: "+3220000000",
            StartUtc: null, Direction: direction);

    /// Convenience: most priors don't care about the talk-time anchor, so
    /// default it to null and let the anchor-specific tests pass one.
    private static TelavoxCallStateSnapshot Prior(
        string? callId, string? state, string? direction, DateTime seenUtc,
        DateTime? answeredAtUtc = null) =>
        new(UserId, callId, state, direction, answeredAtUtc, seenUtc);

    [Fact]
    public void NoCall_ClearsBaseline_NoFire()
    {
        var prior = Prior("call-1", "ANSWERED", "incoming", Now.AddSeconds(-2), Now.AddSeconds(-20));
        var d = TelavoxCallTransition.Evaluate(prior, current: null,
            TelavoxCallTransition.Answered, UserId, Now);

        Assert.False(d.ShouldFire);
        Assert.Null(d.NewBaseline.LastCallId);
        Assert.Null(d.NewBaseline.LastState);
        Assert.Null(d.NewBaseline.LastDirection);
        Assert.Null(d.NewBaseline.AnsweredAtUtc);
        Assert.Equal(Now, d.NewBaseline.LastSeenUtc);
    }

    [Fact]
    public void Answered_NewCallId_AlreadyAnswered_Fires()
    {
        // First poll observes the call already answered (worker started
        // mid-call, or poll missed the RINGING window). Mode "answered"
        // must still fire so the popup isn't permanently silenced after a
        // restart.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "ANSWERED"),
            TelavoxCallTransition.Answered, UserId, Now);

        Assert.True(d.ShouldFire);
        Assert.Equal("call-1", d.NewBaseline.LastCallId);
        Assert.Equal("ANSWERED", d.NewBaseline.LastState);
    }

    [Fact]
    public void Answered_NewCallId_StillRinging_DoesNotFire()
    {
        // Mode "answered" is the default: ignore RINGING-only observations.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "RINGING"),
            TelavoxCallTransition.Answered, UserId, Now);

        Assert.False(d.ShouldFire);
        Assert.Equal("call-1", d.NewBaseline.LastCallId);
        Assert.Equal("RINGING", d.NewBaseline.LastState);
    }

    [Fact]
    public void Answered_RingingToAnswered_Fires()
    {
        // The flagship transition: same callId, state edge RINGING → ANSWERED.
        var prior = Prior("call-1", "RINGING", "incoming", Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "ANSWERED"),
            TelavoxCallTransition.Answered, UserId, Now);

        Assert.True(d.ShouldFire);
        Assert.Equal("call-1", d.NewBaseline.LastCallId);
        Assert.Equal("ANSWERED", d.NewBaseline.LastState);
    }

    [Fact]
    public void Answered_SameCallSameState_DoesNotFire()
    {
        // Long-running ANSWERED call must not re-pop on every tick.
        var prior = Prior("call-1", "ANSWERED", "incoming", Now.AddSeconds(-30), Now.AddSeconds(-30));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "ANSWERED"),
            TelavoxCallTransition.Answered, UserId, Now);

        Assert.False(d.ShouldFire);
        Assert.Equal("call-1", d.NewBaseline.LastCallId);
        Assert.Equal("ANSWERED", d.NewBaseline.LastState);
    }

    [Fact]
    public void Ringing_NewCallId_StateRinging_Fires()
    {
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "RINGING"),
            TelavoxCallTransition.Ringing, UserId, Now);
        Assert.True(d.ShouldFire);
    }

    [Fact]
    public void Ringing_NewCallId_StateAnswered_DoesNotFire()
    {
        // In "ringing" mode an already-answered call missed the edge —
        // the SPA wants the ringing toast; if we missed it, the train left.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "ANSWERED"),
            TelavoxCallTransition.Ringing, UserId, Now);
        Assert.False(d.ShouldFire);
    }

    [Fact]
    public void Either_NewRingingCall_Fires()
    {
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "RINGING"),
            TelavoxCallTransition.Either, UserId, Now);
        Assert.True(d.ShouldFire);
    }

    [Fact]
    public void Either_RingingToAnswered_FiresAgain()
    {
        // Mode "either": the ringing edge already fired; the answered edge
        // is still a meaningful transition (popup swaps from "ringing" to
        // "live"). The worker upserts the baseline so the next ANSWERED
        // tick is a same-state debounce, not a re-fire.
        var prior = Prior("call-1", "RINGING", "incoming", Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "ANSWERED"),
            TelavoxCallTransition.Either, UserId, Now);
        Assert.True(d.ShouldFire);
    }

    [Fact]
    public void NewCallReplacesPriorCall_FiresOnAnsweredEdge()
    {
        // Caller A hung up (ENDED), caller B is now active. Different
        // callId means we evaluate as a fresh observation.
        var prior = Prior("call-1", "ENDED", "incoming", Now.AddSeconds(-5));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-2", "ANSWERED"),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.True(d.ShouldFire);
        Assert.Equal("call-2", d.NewBaseline.LastCallId);
    }

    [Fact]
    public void UnknownTriggerMode_FailsToAnsweredDefault()
    {
        // Misconfigured setting (typo, ancient seed) must not spam-fire.
        var prior = Prior("call-1", "RINGING", "incoming", Now.AddSeconds(-1));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "ANSWERED"),
            triggerMode: "whatever", UserId, Now);
        // Fails over to "answered" semantics, so this transition fires.
        Assert.True(d.ShouldFire);
    }

    [Fact]
    public void AlertingSynonym_TreatedAsRinging()
    {
        // Some PBX vocabularies use ALERTING instead of RINGING. The
        // transition module treats them as equivalent so a CAPI vocab tweak
        // doesn't silently break the popup.
        var prior = Prior("call-1", "ALERTING", "incoming", Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "ANSWERED"),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.True(d.ShouldFire);
    }

    [Fact]
    public void Ringing_AlertingToRinging_DoesNotRefire()
    {
        // CAPI flips its synonym mid-call (ALERTING → RINGING). The
        // baseline string changes, but IsRinging treats both as ringing,
        // so the edge-detector must NOT re-fire — the ringing toast is
        // already up on the agent's screen.
        var prior = Prior("call-1", "ALERTING", "incoming", Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "RINGING"),
            TelavoxCallTransition.Ringing, UserId, Now);
        Assert.False(d.ShouldFire);
        // Baseline DOES update so the next state-edge compares against
        // the freshest upstream value.
        Assert.Equal("RINGING", d.NewBaseline.LastState);
    }

    [Fact]
    public void IdleToCall_Ringing_FiresInRingingMode()
    {
        // Prior baseline was idle (last_call_id = null) → fresh callId.
        var prior = Prior(null, null, null, Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-7", "RINGING"),
            TelavoxCallTransition.Ringing, UserId, Now);
        Assert.True(d.ShouldFire);
        Assert.Equal("call-7", d.NewBaseline.LastCallId);
    }

    [Fact]
    public void DialingState_DoesNotFire()
    {
        // Outbound DIALING state: not RINGING, not ANSWERED — silent.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "DIALING"),
            TelavoxCallTransition.Either, UserId, Now);
        Assert.False(d.ShouldFire);
    }

    // ---- direction-aware popup gating (v0.0.78) ----

    [Fact]
    public void Outgoing_AnsweredCall_NeverFires_ButTracksBaseline()
    {
        // The agent dialled out. Even in "either" mode (the loudest) the
        // popup must stay silent — but the baseline still records the call
        // (state + direction) so the dashboard indicator flips to busy.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-9", "up", TelavoxCallDirection.Outgoing),
            TelavoxCallTransition.Either, UserId, Now);

        Assert.False(d.ShouldFire);
        Assert.Equal("call-9", d.NewBaseline.LastCallId);
        Assert.Equal("up", d.NewBaseline.LastState);
        Assert.Equal("outgoing", d.NewBaseline.LastDirection);
    }

    [Fact]
    public void Outgoing_RingingToAnswered_NeverFires()
    {
        // A full outbound ring→answer edge that would fire for an inbound
        // call must stay silent because the direction is outgoing.
        var prior = Prior("call-9", "ring", "outgoing", Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-9", "up", TelavoxCallDirection.Outgoing),
            TelavoxCallTransition.Either, UserId, Now);
        Assert.False(d.ShouldFire);
        Assert.Equal("up", d.NewBaseline.LastState);
    }

    [Fact]
    public void Incoming_DirectionCarriedIntoBaseline()
    {
        // The inbound direction rides along on the baseline so the
        // completed-call activity row can label it on the answered→idle edge.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-3", "ringing", TelavoxCallDirection.Incoming),
            TelavoxCallTransition.Ringing, UserId, Now);
        Assert.True(d.ShouldFire);
        Assert.Equal("incoming", d.NewBaseline.LastDirection);
    }

    [Fact]
    public void UnknownDirection_TreatedAsInbound_StillFires()
    {
        // A CAPI row with no callDirection must not silence a real inbound
        // popup — absence is treated as inbound (pre-direction behaviour).
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-4", "up", direction: ""),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.True(d.ShouldFire);
    }

    // ---- talk-time anchor (v0.0.78) ----

    [Fact]
    public void Answered_FirstObservation_AnchorsAtNow()
    {
        // First answered tick stamps the talk-time anchor at "now" so the
        // completed-call row can later measure connected time.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "up"),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.Equal(Now, d.NewBaseline.AnsweredAtUtc);
    }

    [Fact]
    public void Answered_SameCall_HoldsOriginalAnchorSteady()
    {
        // A long answered call must keep its first anchor across ticks so the
        // duration is measured from the true pickup, not the latest poll.
        var anchored = Now.AddSeconds(-45);
        var prior = Prior("call-1", "up", "incoming", Now.AddSeconds(-1), anchored);
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "up"),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.Equal(anchored, d.NewBaseline.AnsweredAtUtc);
    }

    [Fact]
    public void Ringing_HasNoAnchorYet()
    {
        // Only ringing — talk hasn't started, so no anchor.
        var d = TelavoxCallTransition.Evaluate(
            prior: null, current: MakeCall("call-1", "ringing"),
            TelavoxCallTransition.Ringing, UserId, Now);
        Assert.Null(d.NewBaseline.AnsweredAtUtc);
    }

    [Fact]
    public void RingingToAnswered_AnchorsAtAnswerTime_NotRingTime()
    {
        // The prior tick was ringing (no anchor). The answered edge anchors
        // at "now", so the duration excludes the ring phase (talk-time).
        var prior = Prior("call-1", "ringing", "incoming", Now.AddSeconds(-8));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-1", "up"),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.Equal(Now, d.NewBaseline.AnsweredAtUtc);
    }

    [Fact]
    public void NewAnsweredCallAfterPriorCall_ReanchorsAtNow()
    {
        // A different callId means a fresh anchor even if the prior call was
        // also answered — the old anchor must not leak into the new call.
        var prior = Prior("call-1", "up", "incoming", Now.AddSeconds(-1), Now.AddSeconds(-90));
        var d = TelavoxCallTransition.Evaluate(
            prior, current: MakeCall("call-2", "up"),
            TelavoxCallTransition.Answered, UserId, Now);
        Assert.Equal(Now, d.NewBaseline.AnsweredAtUtc);
    }
}
