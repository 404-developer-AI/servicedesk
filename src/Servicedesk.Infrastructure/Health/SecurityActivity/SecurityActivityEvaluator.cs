namespace Servicedesk.Infrastructure.Health.SecurityActivity;

/// Pure function that turns raw counts + thresholds into a snapshot. Lives
/// outside the monitor so its threshold logic can be unit-tested without
/// spinning up a background service or any IO.
public static class SecurityActivityEvaluator
{
    public static SecurityActivitySnapshot Evaluate(
        IReadOnlyDictionary<string, int> countsByEventType,
        IReadOnlyDictionary<string, int> thresholdsByCategoryKey,
        int criticalMultiplier,
        TimeSpan window,
        DateTime nowUtc,
        bool monitorEnabled,
        DateTime? acknowledgedFromUtc = null)
    {
        var multiplier = criticalMultiplier < 1 ? 1 : criticalMultiplier;
        var rollup = HealthStatus.Ok;
        var results = new List<SecurityActivityCategoryResult>(SecurityActivityCategories.All.Count);
        var hotParts = new List<string>();

        foreach (var category in SecurityActivityCategories.All)
        {
            // Sum counts across every audit event-type that maps to this
            // category — e.g. the M365-rejected category aggregates five
            // separate reject reasons into one bucket.
            var count = 0;
            foreach (var evt in category.EventTypes)
            {
                if (countsByEventType.TryGetValue(evt, out var c)) count += c;
            }

            var threshold = thresholdsByCategoryKey.TryGetValue(category.Key, out var t)
                ? Math.Max(1, t)
                : category.DefaultThreshold;
            var critical = checked(threshold * multiplier);

            HealthStatus status;
            if (count >= critical)
            {
                status = HealthStatus.Critical;
                hotParts.Add($"{category.Label}: {count}");
            }
            else if (count >= threshold)
            {
                status = HealthStatus.Warning;
                hotParts.Add($"{category.Label}: {count}");
            }
            else
            {
                status = HealthStatus.Ok;
            }

            if (status > rollup) rollup = status;

            results.Add(new SecurityActivityCategoryResult(
                Key: category.Key,
                Label: category.Label,
                Count: count,
                Threshold: threshold,
                CriticalThreshold: critical,
                Status: status));
        }

        var windowText = FormatWindow(window);
        var sinceText = acknowledgedFromUtc is { } ack
            ? $"since the last acknowledge ({ack:HH:mm:ss} UTC)"
            : $"in the last {windowText}";

        string summary;
        if (!monitorEnabled)
        {
            summary = "Monitoring disabled — security activity is not sampled.";
        }
        else if (rollup == HealthStatus.Ok)
        {
            var total = results.Sum(r => r.Count);
            summary = total == 0
                ? $"No notable security activity {sinceText}."
                : $"{total} security event(s) {sinceText} — all categories below threshold.";
        }
        else
        {
            summary = $"Elevated security activity {sinceText} — {string.Join(", ", hotParts)}.";
        }

        return new SecurityActivitySnapshot(
            EvaluatedUtc: nowUtc,
            Window: window,
            Status: monitorEnabled ? rollup : HealthStatus.Ok,
            Summary: summary,
            Categories: results,
            MonitorEnabled: monitorEnabled,
            AcknowledgedFromUtc: acknowledgedFromUtc);
    }

    private static string FormatWindow(TimeSpan window)
    {
        if (window.TotalHours >= 1 && window.TotalSeconds % 3600 == 0)
            return window.TotalHours == 1 ? "hour" : $"{(int)window.TotalHours} hours";
        if (window.TotalMinutes >= 1 && window.TotalSeconds % 60 == 0)
            return window.TotalMinutes == 1 ? "minute" : $"{(int)window.TotalMinutes} minutes";
        return $"{(int)window.TotalSeconds} seconds";
    }
}
