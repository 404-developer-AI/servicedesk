using Servicedesk.Domain.Sla;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.24 — pins the business-day + wake-time arithmetic used by the
/// `set_pending_till` action's "businessDays" mode. The user-facing
/// scenario (the one that drove the feature): a ticket goes to
/// "Waiting for Customer" Mon 18:00; an auto-bump trigger should set
/// pending-till to Thu 08:00 (skip the tail of Mon, all of Tue and
/// Wed = 2 business days, wake on the next business morning).
public sealed class BusinessDayPendingCalculatorTests
{
    /// Mon-Fri 09:00–17:00 in UTC (avoid TZ surprises in test asserts).
    private static BusinessHoursSchema MonFriUtcSchema(IEnumerable<DateOnly>? holidays = null) =>
        new(
            Id: Guid.NewGuid(),
            Name: "Test",
            Timezone: "UTC",
            CountryCode: "BE",
            IsDefault: true,
            Slots: new[]
            {
                new BusinessHoursSlot(1, Guid.Empty, 1, 9 * 60, 17 * 60), // Mon
                new BusinessHoursSlot(2, Guid.Empty, 2, 9 * 60, 17 * 60), // Tue
                new BusinessHoursSlot(3, Guid.Empty, 3, 9 * 60, 17 * 60), // Wed
                new BusinessHoursSlot(4, Guid.Empty, 4, 9 * 60, 17 * 60), // Thu
                new BusinessHoursSlot(5, Guid.Empty, 5, 9 * 60, 17 * 60), // Fri
            },
            Holidays: (holidays ?? Array.Empty<DateOnly>())
                .Select(d => new Holiday(0, Guid.Empty, d, "test", "manual", "BE"))
                .ToList());

    [Fact]
    public void Mon_evening_plus_two_business_days_at_eight_lands_on_thursday()
    {
        // Mon 27 Apr 2026 18:00 UTC. April 27 2026 is a Monday.
        var nowUtc = new DateTime(2026, 4, 27, 18, 0, 0, DateTimeKind.Utc);
        var schema = MonFriUtcSchema();

        var target = BusinessDayPendingCalculator.Resolve(
            nowUtc, businessDays: 2, wakeAtLocal: TimeSpan.FromHours(8), schema);

        Assert.Equal(new DateTime(2026, 4, 30, 8, 0, 0, DateTimeKind.Utc), target);
    }

    [Fact]
    public void Friday_evening_skips_weekend_and_lands_on_wednesday()
    {
        // Fri 1 May 2026 18:00 UTC. Day-0 wake = Mon 4 May 08:00, +2 = Wed 6 May 08:00.
        var nowUtc = new DateTime(2026, 5, 1, 18, 0, 0, DateTimeKind.Utc);
        var schema = MonFriUtcSchema();

        var target = BusinessDayPendingCalculator.Resolve(
            nowUtc, businessDays: 2, wakeAtLocal: TimeSpan.FromHours(8), schema);

        Assert.Equal(new DateTime(2026, 5, 6, 8, 0, 0, DateTimeKind.Utc), target);
    }

    [Fact]
    public void Same_day_future_wake_time_counts_as_day_zero()
    {
        // Tue 28 Apr 2026 06:00 UTC, wake at 08:00 → today is day 0.
        // +2 business days → Thu 30 Apr 08:00.
        var nowUtc = new DateTime(2026, 4, 28, 6, 0, 0, DateTimeKind.Utc);
        var schema = MonFriUtcSchema();

        var target = BusinessDayPendingCalculator.Resolve(
            nowUtc, businessDays: 2, wakeAtLocal: TimeSpan.FromHours(8), schema);

        Assert.Equal(new DateTime(2026, 4, 30, 8, 0, 0, DateTimeKind.Utc), target);
    }

    [Fact]
    public void Zero_business_days_returns_next_wake_moment()
    {
        // Mon 27 Apr 2026 18:00 UTC, businessDays=0 → next 08:00 = Tue 28 Apr 08:00.
        var nowUtc = new DateTime(2026, 4, 27, 18, 0, 0, DateTimeKind.Utc);
        var schema = MonFriUtcSchema();

        var target = BusinessDayPendingCalculator.Resolve(
            nowUtc, businessDays: 0, wakeAtLocal: TimeSpan.FromHours(8), schema);

        Assert.Equal(new DateTime(2026, 4, 28, 8, 0, 0, DateTimeKind.Utc), target);
    }

    [Fact]
    public void Holiday_is_skipped_in_the_walk()
    {
        // Tue 28 Apr 2026 is marked as a holiday — Mon 18:00 + 2 BD should
        // skip Tue and land on Fri 1 May 08:00 (Wed=day1, Thu=day2 isn't
        // right; let's compute: day-0 = Wed 29 Apr (Tue was holiday so
        // first business day after Mon evening is Wed). +1 = Thu, +2 = Fri).
        var holiday = new DateOnly(2026, 4, 28);
        var nowUtc = new DateTime(2026, 4, 27, 18, 0, 0, DateTimeKind.Utc);
        var schema = MonFriUtcSchema(new[] { holiday });

        var target = BusinessDayPendingCalculator.Resolve(
            nowUtc, businessDays: 2, wakeAtLocal: TimeSpan.FromHours(8), schema);

        Assert.Equal(new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc), target);
    }

    [Fact]
    public void Empty_slots_throws_clear_error()
    {
        var schema = new BusinessHoursSchema(
            Id: Guid.NewGuid(), Name: "Empty", Timezone: "UTC", CountryCode: "BE",
            IsDefault: true, Slots: Array.Empty<BusinessHoursSlot>(), Holidays: Array.Empty<Holiday>());
        var nowUtc = new DateTime(2026, 4, 27, 18, 0, 0, DateTimeKind.Utc);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BusinessDayPendingCalculator.Resolve(nowUtc, 2, TimeSpan.FromHours(8), schema));
        Assert.Contains("no working slots", ex.Message);
    }

    [Fact]
    public void Brussels_timezone_wakes_at_local_eight_not_utc_eight()
    {
        // Schema in Europe/Brussels (UTC+2 on 30 Apr 2026, DST). Local
        // 08:00 = 06:00 UTC. The expected UTC target proves the
        // calculator did the TZ conversion both ways.
        var nowUtc = new DateTime(2026, 4, 27, 16, 0, 0, DateTimeKind.Utc); // Mon 18:00 local Brussels
        var schema = MonFriUtcSchema() with { Timezone = "Europe/Brussels" };

        var target = BusinessDayPendingCalculator.Resolve(
            nowUtc, businessDays: 2, wakeAtLocal: TimeSpan.FromHours(8), schema);

        // Thu 30 Apr 08:00 Brussels (DST active = UTC+2) → 06:00 UTC.
        Assert.Equal(new DateTime(2026, 4, 30, 6, 0, 0, DateTimeKind.Utc), target);
    }

    [Fact]
    public void Negative_business_days_throws()
    {
        var nowUtc = new DateTime(2026, 4, 27, 18, 0, 0, DateTimeKind.Utc);
        var schema = MonFriUtcSchema();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BusinessDayPendingCalculator.Resolve(nowUtc, -1, TimeSpan.FromHours(8), schema));
    }
}
