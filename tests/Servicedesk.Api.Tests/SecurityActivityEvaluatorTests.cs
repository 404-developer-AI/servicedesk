using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Health.SecurityActivity;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SecurityActivityEvaluatorTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly DateTime Now = new(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_activity_returns_ok_with_empty_summary()
    {
        var snap = Evaluate(counts: new Dictionary<string, int>(), monitorEnabled: true);

        Assert.Equal(HealthStatus.Ok, snap.Status);
        Assert.Contains("No notable security activity", snap.Summary);
        Assert.All(snap.Categories, c => Assert.Equal(HealthStatus.Ok, c.Status));
    }

    [Fact]
    public void Disabled_monitor_returns_ok_even_with_activity()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 999 };

        var snap = Evaluate(counts, monitorEnabled: false);

        Assert.Equal(HealthStatus.Ok, snap.Status);
        Assert.Contains("Monitoring disabled", snap.Summary);
        Assert.False(snap.MonitorEnabled);
    }

    [Fact]
    public void Count_at_threshold_flips_category_to_warning()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 10 };

        var snap = Evaluate(counts, monitorEnabled: true, loginFailedThreshold: 10, multiplier: 3);

        Assert.Equal(HealthStatus.Warning, snap.Status);
        var hot = snap.Categories.Single(c => c.Key == "login_failed");
        Assert.Equal(HealthStatus.Warning, hot.Status);
        Assert.Equal(30, hot.CriticalThreshold);
        Assert.Contains("Failed logins", snap.Summary);
    }

    [Fact]
    public void Count_at_critical_multiple_flips_category_to_critical()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 30 };

        var snap = Evaluate(counts, monitorEnabled: true, loginFailedThreshold: 10, multiplier: 3);

        Assert.Equal(HealthStatus.Critical, snap.Status);
        Assert.Equal(HealthStatus.Critical, snap.Categories.Single(c => c.Key == "login_failed").Status);
    }

    [Fact]
    public void Multiplier_of_one_skips_warning_and_goes_straight_to_critical()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 10 };

        var snap = Evaluate(counts, monitorEnabled: true, loginFailedThreshold: 10, multiplier: 1);

        Assert.Equal(HealthStatus.Critical, snap.Status);
    }

    [Fact]
    public void Microsoft_login_rejected_category_sums_all_five_reject_reasons()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["auth.microsoft.login.rejected_unknown"] = 2,
            ["auth.microsoft.login.rejected_disabled"] = 1,
            ["auth.microsoft.login.rejected_customer"] = 1,
            ["auth.microsoft.login.rejected_inactive"] = 1,
            ["auth.microsoft.login.failed_callback"] = 0,
            // 5 total — equals default threshold of 5 → Warning
        };

        var snap = Evaluate(counts, monitorEnabled: true, msRejectedThreshold: 5, multiplier: 3);

        var ms = snap.Categories.Single(c => c.Key == "microsoft_login_rejected");
        Assert.Equal(5, ms.Count);
        Assert.Equal(HealthStatus.Warning, ms.Status);
    }

    [Fact]
    public void Rollup_takes_highest_category_status()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 10,          // Warning @10
            ["csrf_rejected"] = 20,         // Critical @15 (threshold 5 × 3)
        };

        var snap = Evaluate(counts, monitorEnabled: true,
            loginFailedThreshold: 10, csrfThreshold: 5, multiplier: 3);

        Assert.Equal(HealthStatus.Critical, snap.Status);
    }

    [Fact]
    public void Threshold_below_one_is_clamped_to_one()
    {
        // Defensive — a zero / negative threshold would mean "trip on
        // literally zero events", which is a footgun. Clamp to 1.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 1 };

        var snap = Evaluate(counts, monitorEnabled: true, loginFailedThreshold: 0, multiplier: 3);

        Assert.Equal(HealthStatus.Warning,
            snap.Categories.Single(c => c.Key == "login_failed").Status);
    }

    private static SecurityActivitySnapshot Evaluate(
        IReadOnlyDictionary<string, int> counts,
        bool monitorEnabled,
        int loginFailedThreshold = 10,
        int lockedOutThreshold = 3,
        int csrfThreshold = 5,
        int rateLimitedThreshold = 50,
        int msRejectedThreshold = 5,
        int multiplier = 3)
    {
        var thresholds = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = loginFailedThreshold,
            ["login_locked_out"] = lockedOutThreshold,
            ["csrf_rejected"] = csrfThreshold,
            ["rate_limited"] = rateLimitedThreshold,
            ["microsoft_login_rejected"] = msRejectedThreshold,
        };

        return SecurityActivityEvaluator.Evaluate(
            countsByEventType: counts,
            thresholdsByCategoryKey: thresholds,
            criticalMultiplier: multiplier,
            window: Window,
            nowUtc: Now,
            monitorEnabled: monitorEnabled);
    }
}
