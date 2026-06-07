namespace Servicedesk.Infrastructure.Statistics;

/// Canonical string values for the statistic-tile definition fields. The
/// database stores these as plain TEXT (no CHECK constraint) so the
/// catalogue can grow without a migration; validation happens here in code.
public static class StatisticMetricKeys
{
    /// Sum of timesheet minutes over the period, optionally grouped.
    public const string WorkedHours = "worked_hours";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { WorkedHours };

    public static bool IsKnown(string? key) => key is not null && All.Contains(key);
}

public static class StatisticChartTypes
{
    public const string Kpi = "kpi";
    public const string Bar = "bar";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Kpi, Bar };

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

public static class StatisticPeriods
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public const string Year = "year";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Day, Week, Month, Year };

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

public static class StatisticGroupings
{
    /// One total for the whole period (a KPI, or a single bar).
    public const string None = "none";

    /// One value per timesheet task (Servicedesk / Administration / …).
    public const string Task = "task";

    /// One value per time bucket inside the period (days in a week/month,
    /// months in a year, hours in a day).
    public const string Time = "time";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { None, Task, Time };

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

public static class StatisticScopes
{
    /// Rebinds to whoever is viewing the tile — every read-agent sees their
    /// own figures from the same tile definition.
    public const string ViewerSelf = "viewer_self";

    /// A single fixed technician (scope_user_id on the tile).
    public const string User = "user";

    /// All Agent + Admin users together.
    public const string Team = "team";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { ViewerSelf, User, Team };

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

public static class StatisticTileSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Wide = "wide";
    public const string Full = "full";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Small, Medium, Wide, Full };

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

/// One metric entry advertised to the builder UI: which chart types and
/// groupings it supports, and its unit. Returned by the catalogue endpoint
/// so the builder only offers valid combinations.
public sealed record StatisticMetricDescriptor(
    string Key,
    string Label,
    string Unit,
    IReadOnlyList<string> ChartTypes,
    IReadOnlyList<string> Groupings,
    bool SupportsScope);

public static class StatisticsCatalogue
{
    /// The v1 catalogue. Grows one entry per metric as the feature expands.
    public static readonly IReadOnlyList<StatisticMetricDescriptor> Metrics = new[]
    {
        new StatisticMetricDescriptor(
            Key: StatisticMetricKeys.WorkedHours,
            Label: "Worked hours",
            Unit: "hours",
            ChartTypes: new[] { StatisticChartTypes.Kpi, StatisticChartTypes.Bar },
            Groupings: new[] { StatisticGroupings.None, StatisticGroupings.Task, StatisticGroupings.Time },
            SupportsScope: true),
    };

    public static StatisticMetricDescriptor? Find(string? key) =>
        key is null ? null : Metrics.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
}
