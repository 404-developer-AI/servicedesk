using Servicedesk.Infrastructure.Integrations.Telavox;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.34 commit C — pins the working-hours window evaluator in
/// <see cref="TelavoxPollWindow"/>. Midnight-wrap, day-mask, and
/// malformed-input fail-open behaviour are all admin-facing edge cases the
/// worker would otherwise be hard to test against directly.
public sealed class TelavoxPollWindowTests
{
    private const string Workweek = "true,true,true,true,true,false,false";
    private const string AllDays = "true,true,true,true,true,true,true";

    [Fact]
    public void Midday_Workweek_InsideWindow()
    {
        // Monday 2026-05-11 at 12:00 — well inside 08:00-18:00.
        var now = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(now, "08:00", "18:00", Workweek));
    }

    [Fact]
    public void Saturday_Workweek_OutsideWindow()
    {
        // Saturday 2026-05-09 — workweek mask blocks Sat/Sun.
        var now = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.False(TelavoxPollWindow.IsInPollWindow(now, "08:00", "18:00", Workweek));
    }

    [Fact]
    public void Saturday_AllDays_InsideWindow()
    {
        var now = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(now, "08:00", "18:00", AllDays));
    }

    [Fact]
    public void BeforeStart_OutsideWindow()
    {
        var now = new DateTime(2026, 5, 11, 7, 59, 0, DateTimeKind.Unspecified);
        Assert.False(TelavoxPollWindow.IsInPollWindow(now, "08:00", "18:00", Workweek));
    }

    [Fact]
    public void AtStart_InsideWindow_AtEnd_Outside()
    {
        // Inclusive at start, exclusive at end — same convention as a
        // half-open interval [start, end).
        var atStart = new DateTime(2026, 5, 11, 8, 0, 0, DateTimeKind.Unspecified);
        var atEnd = new DateTime(2026, 5, 11, 18, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(atStart, "08:00", "18:00", Workweek));
        Assert.False(TelavoxPollWindow.IsInPollWindow(atEnd, "08:00", "18:00", Workweek));
    }

    [Fact]
    public void MidnightWrap_InsideWindow_AfterMidnight()
    {
        // Night-shift 22:00 → 06:00. Tuesday 03:00 is inside the window.
        var now = new DateTime(2026, 5, 12, 3, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(now, "22:00", "06:00", AllDays));
    }

    [Fact]
    public void MidnightWrap_OutsideWindow_LateMorning()
    {
        var now = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.False(TelavoxPollWindow.IsInPollWindow(now, "22:00", "06:00", AllDays));
    }

    [Fact]
    public void EqualStartEnd_TreatedAsAlwaysOn()
    {
        // The setting docs call out start == end as the "window disabled"
        // sentinel for installs that want 24/7 polling.
        var now = new DateTime(2026, 5, 11, 23, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(now, "08:00", "08:00", AllDays));
    }

    [Fact]
    public void MalformedTime_FailsOpen()
    {
        // An admin's typo must not silently disable the integration; the
        // poll-attempts still surface in integration_audit either way.
        var now = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(now, "garbage", "18:00", Workweek));
        Assert.True(TelavoxPollWindow.IsInPollWindow(now, "08:00", "25:99", Workweek));
    }

    [Fact]
    public void MalformedDayMask_FailsOpen()
    {
        // Symmetric to the time-of-day parser: malformed mask = all-days,
        // so a typo never silently weekend-disables the integration. The
        // attempt + audit row still surface in integration_audit, which is
        // how the admin notices.
        var sat = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Unspecified);
        var sun = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.True(TelavoxPollWindow.IsInPollWindow(sat, "08:00", "18:00", "nope,nope"));
        Assert.True(TelavoxPollWindow.IsInPollWindow(sun, "08:00", "18:00", "garbage,bool,not,seven,parts,here,bad"));
    }

    [Fact]
    public void EmptyDayMask_UsesWorkweekDefault()
    {
        // Empty = "admin never touched it" → workweek default (mirrors the
        // seeded SettingDefault). Distinct from malformed = fail-open.
        var sat = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Unspecified);
        var mon = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.False(TelavoxPollWindow.IsInPollWindow(sat, "08:00", "18:00", ""));
        Assert.True(TelavoxPollWindow.IsInPollWindow(mon, "08:00", "18:00", ""));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 0)]
    [InlineData(DayOfWeek.Tuesday, 1)]
    [InlineData(DayOfWeek.Sunday, 6)]
    public void MaskIndex_MapsMondayFirst(DayOfWeek dow, int expectedIndex)
    {
        Assert.Equal(expectedIndex, TelavoxPollWindow.MaskIndexForDay(dow));
    }

    [Theory]
    [InlineData("", true)] // empty → default workweek; Monday is open
    [InlineData("true,false,true,false,true,false,true", true)] // Mon=true
    [InlineData("false,true,true,true,true,true,true", false)] // Mon=false
    public void DayMask_MondaySlot(string raw, bool expectedMonOpen)
    {
        var mask = TelavoxPollWindow.ParseDayMask(raw);
        Assert.Equal(expectedMonOpen, mask[0]);
    }
}
