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

    private static TelavoxCall MakeCall(string id, string state) =>
        new(id, state, FromNumber: "+3245555555", ToNumber: "+3220000000", StartUtc: null);

    [Fact]
    public void NoCall_ClearsBaseline_NoFire()
    {
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "ANSWERED", Now.AddSeconds(-2));
        var d = TelavoxCallTransition.Evaluate(prior, current: null,
            TelavoxCallTransition.Answered, UserId, Now);

        Assert.False(d.ShouldFire);
        Assert.Null(d.NewBaseline.LastCallId);
        Assert.Null(d.NewBaseline.LastState);
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "RINGING", Now.AddSeconds(-2));
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "ANSWERED", Now.AddSeconds(-30));
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "RINGING", Now.AddSeconds(-2));
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "ENDED", Now.AddSeconds(-5));
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "RINGING", Now.AddSeconds(-1));
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "ALERTING", Now.AddSeconds(-2));
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
        var prior = new TelavoxCallStateSnapshot(UserId, "call-1", "ALERTING", Now.AddSeconds(-2));
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
        var prior = new TelavoxCallStateSnapshot(UserId, null, null, Now.AddSeconds(-2));
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
}
