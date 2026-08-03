using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Timesheet;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the UTC→local-day bucketing behind the Tab-3 "Login" column:
/// earliest successful login per local day, kind derived from the audit
/// event type, day boundaries resolved in the app timezone (incl. DST).
public sealed class ManagerTimesheetLoginBucketTests
{
    private static readonly TimeZoneInfo Brussels =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels");

    [Fact]
    public void Earliest_login_of_the_day_wins()
    {
        var logins = new[]
        {
            (new DateTime(2026, 8, 3, 7, 45, 0, DateTimeKind.Utc), AuthEventTypes.LoginSuccess),
            (new DateTime(2026, 8, 3, 6, 12, 0, DateTimeKind.Utc), AuthEventTypes.MicrosoftLoginSuccess),
            (new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc), AuthEventTypes.LoginSuccess),
        };

        var result = ManagerTimesheetService.BucketFirstLogins(logins, Brussels);

        var day = Assert.Single(result);
        Assert.Equal(new DateOnly(2026, 8, 3), day.Key);
        // 06:12 UTC = 08:12 CEST (summer, UTC+2).
        Assert.Equal(8 * 60 + 12, day.Value.Minutes);
        Assert.Equal("microsoft", day.Value.Kind);
    }

    [Fact]
    public void Late_utc_login_lands_on_next_local_day()
    {
        var logins = new[]
        {
            (new DateTime(2026, 8, 3, 22, 30, 0, DateTimeKind.Utc), AuthEventTypes.LoginSuccess),
        };

        var result = ManagerTimesheetService.BucketFirstLogins(logins, Brussels);

        // 22:30 UTC = 00:30 CEST on Aug 4.
        var day = Assert.Single(result);
        Assert.Equal(new DateOnly(2026, 8, 4), day.Key);
        Assert.Equal(30, day.Value.Minutes);
        Assert.Equal("password", day.Value.Kind);
    }

    [Fact]
    public void Winter_time_uses_the_standard_offset()
    {
        var logins = new[]
        {
            (new DateTime(2026, 1, 12, 7, 5, 0, DateTimeKind.Utc), AuthEventTypes.LoginSuccess),
        };

        var result = ManagerTimesheetService.BucketFirstLogins(logins, Brussels);

        // 07:05 UTC = 08:05 CET (winter, UTC+1).
        var day = Assert.Single(result);
        Assert.Equal(new DateOnly(2026, 1, 12), day.Key);
        Assert.Equal(8 * 60 + 5, day.Value.Minutes);
    }

    [Fact]
    public void Logins_spread_over_days_bucket_independently()
    {
        var logins = new[]
        {
            (new DateTime(2026, 8, 3, 6, 0, 0, DateTimeKind.Utc), AuthEventTypes.LoginSuccess),
            (new DateTime(2026, 8, 4, 6, 30, 0, DateTimeKind.Utc), AuthEventTypes.MicrosoftLoginSuccess),
        };

        var result = ManagerTimesheetService.BucketFirstLogins(logins, Brussels);

        Assert.Equal(2, result.Count);
        Assert.Equal("password", result[new DateOnly(2026, 8, 3)].Kind);
        Assert.Equal("microsoft", result[new DateOnly(2026, 8, 4)].Kind);
    }

    [Fact]
    public void Empty_input_yields_empty_map()
    {
        var result = ManagerTimesheetService.BucketFirstLogins(
            Array.Empty<(DateTime, string)>(), Brussels);
        Assert.Empty(result);
    }
}
