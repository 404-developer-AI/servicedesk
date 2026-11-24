namespace Servicedesk.Api.Tests.TestInfrastructure;

/// A <see cref="TimeProvider"/> the test moves by hand: wall clock
/// (<see cref="GetUtcNow"/>) and the monotonic timestamp
/// (<see cref="GetTimestamp"/>, used by elapsed-time checks) both follow
/// <see cref="UtcNow"/>, so expiry and TTL tests never sleep.
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public ManualClock(DateTime startUtc) : this(new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc))) { }

    public DateTimeOffset UtcNow { get; set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => UtcNow.UtcTicks;
}
