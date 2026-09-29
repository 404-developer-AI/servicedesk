using System.Globalization;

namespace Servicedesk.Infrastructure.Insights;

public enum InsightsGranularity { Day, Week, Month, Year }

public enum InsightsPeriod { Today, Week, Month, Year, Custom }

/// An inclusive local-date range in the install's display time zone.
public sealed record InsightsRange(DateOnly From, DateOnly To)
{
    public int DayCount => To.DayNumber - From.DayNumber + 1;
}

/// One chart/table bucket. <see cref="Start"/> is the calendar period key
/// (Monday for a week, the 1st for a month, Jan 1 for a year); From/To are
/// the part of that period that falls inside the requested range, so the
/// first and last bucket can be partial.
public sealed record InsightsBucket(DateOnly Start, DateOnly From, DateOnly To);

/// Pure calendar math for Insights reports: preset periods, bucket layout
/// and the local-midnight → UTC boundaries the SQL filters on. Everything is
/// resolved against the server-side display time zone (App.TimeZone), never
/// the client clock. Kept free of I/O so it is unit-testable.
public static class InsightsCalendar
{
    /// Hard ceiling on buckets per report — a guard against a 10-year range
    /// at day granularity rendering thousands of bars, not a tunable.
    public const int MaxBuckets = 1000;

    /// How far back the ‹ › period stepper may go (in periods).
    public const int MaxOffset = 120;

    public static InsightsRange ResolvePeriod(InsightsPeriod period, int offset, DateOnly today)
    {
        offset = Math.Clamp(offset, -MaxOffset, 0);
        switch (period)
        {
            case InsightsPeriod.Today:
                var day = today.AddDays(offset);
                return new InsightsRange(day, day);
            case InsightsPeriod.Week:
                var monday = StartOfWeek(today).AddDays(7 * offset);
                return new InsightsRange(monday, monday.AddDays(6));
            case InsightsPeriod.Month:
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(offset);
                return new InsightsRange(first, first.AddMonths(1).AddDays(-1));
            case InsightsPeriod.Year:
                var year = today.Year + offset;
                return new InsightsRange(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
            default:
                throw new ArgumentOutOfRangeException(nameof(period), "Custom periods carry their own range.");
        }
    }

    /// Sensible grouping for a range when the caller did not pick one.
    public static InsightsGranularity DefaultGranularity(InsightsRange range) => range.DayCount switch
    {
        <= 62 => InsightsGranularity.Day,
        <= 190 => InsightsGranularity.Week,
        <= 1100 => InsightsGranularity.Month,
        _ => InsightsGranularity.Year,
    };

    public static DateOnly PeriodStart(DateOnly date, InsightsGranularity g) => g switch
    {
        InsightsGranularity.Day => date,
        InsightsGranularity.Week => StartOfWeek(date),
        InsightsGranularity.Month => new DateOnly(date.Year, date.Month, 1),
        InsightsGranularity.Year => new DateOnly(date.Year, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(g)),
    };

    public static DateOnly NextPeriodStart(DateOnly start, InsightsGranularity g) => g switch
    {
        InsightsGranularity.Day => start.AddDays(1),
        InsightsGranularity.Week => start.AddDays(7),
        InsightsGranularity.Month => start.AddMonths(1),
        InsightsGranularity.Year => start.AddYears(1),
        _ => throw new ArgumentOutOfRangeException(nameof(g)),
    };

    /// Number of buckets a range produces, without materialising them.
    public static int CountBuckets(InsightsRange range, InsightsGranularity g) => g switch
    {
        InsightsGranularity.Day => range.DayCount,
        InsightsGranularity.Week => (StartOfWeek(range.To).DayNumber - StartOfWeek(range.From).DayNumber) / 7 + 1,
        InsightsGranularity.Month => (range.To.Year - range.From.Year) * 12 + range.To.Month - range.From.Month + 1,
        InsightsGranularity.Year => range.To.Year - range.From.Year + 1,
        _ => throw new ArgumentOutOfRangeException(nameof(g)),
    };

    public static IReadOnlyList<InsightsBucket> BuildBuckets(InsightsRange range, InsightsGranularity g)
    {
        var buckets = new List<InsightsBucket>();
        var start = PeriodStart(range.From, g);
        while (start <= range.To)
        {
            var next = NextPeriodStart(start, g);
            var from = start < range.From ? range.From : start;
            var lastDay = next.AddDays(-1);
            var to = lastDay > range.To ? range.To : lastDay;
            buckets.Add(new InsightsBucket(start, from, to));
            start = next;
        }
        return buckets;
    }

    /// UTC instant of local midnight on <paramref name="date"/>. Where a DST
    /// jump skips midnight the first valid local instant after it is used.
    public static DateTime LocalMidnightUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// Lower UTC boundary of every bucket, ascending — fed to Postgres
    /// <c>width_bucket(created_utc, bounds)</c> so bucketing happens in SQL
    /// but stays DST-exact per the .NET zone rules.
    public static DateTime[] BucketBoundsUtc(IReadOnlyList<InsightsBucket> buckets, TimeZoneInfo tz)
        => buckets.Select(b => LocalMidnightUtc(b.From, tz)).ToArray();

    /// Display-timezone resolution, same order as /api/system/time:
    /// App.TimeZone → container local zone → UTC.
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* invalid id — fall through */ }
        }
        return TimeZoneInfo.Local;
    }

    public static DateOnly StartOfWeek(DateOnly date)
    {
        // ISO weeks: Monday first.
        var diff = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-diff);
    }

    /// Bucket label shared by the PDF so it reads the same as the UI.
    public static string FormatBucketLabel(DateOnly start, InsightsGranularity g) => g switch
    {
        InsightsGranularity.Day => start.ToString("ddd d MMM", CultureInfo.InvariantCulture),
        InsightsGranularity.Week => $"W{ISOWeek.GetWeekOfYear(start.ToDateTime(TimeOnly.MinValue)):00} · {start.ToString("d MMM", CultureInfo.InvariantCulture)}",
        InsightsGranularity.Month => start.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        InsightsGranularity.Year => start.ToString("yyyy", CultureInfo.InvariantCulture),
        _ => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    public static string FormatRange(InsightsRange range)
    {
        static string D(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return range.From == range.To ? D(range.From) : $"{D(range.From)} – {D(range.To)}";
    }
}
