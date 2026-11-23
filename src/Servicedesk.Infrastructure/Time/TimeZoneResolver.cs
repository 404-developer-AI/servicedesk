namespace Servicedesk.Infrastructure.Time;

public enum TimeZoneLookup
{
    Found,
    NotFound,
    Invalid,
}

/// The one place that turns a configured IANA / Windows time-zone id into a
/// <see cref="TimeZoneInfo"/>. Callers pick their own fallback (UTC, or the
/// server-local zone) — the fallback is part of each surface's behaviour.
public static class TimeZoneResolver
{
    /// Blank, unknown or malformed ids resolve to <paramref name="fallback"/>.
    public static TimeZoneInfo Resolve(string? id, TimeZoneInfo fallback)
    {
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return fallback; }
    }

    /// Distinguishes an unknown id from a malformed one for callers that
    /// report the two differently. A blank id is <see cref="TimeZoneLookup.NotFound"/>.
    public static TimeZoneLookup Lookup(string? id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneLookup.NotFound;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return TimeZoneLookup.Found;
        }
        catch (TimeZoneNotFoundException) { return TimeZoneLookup.NotFound; }
        catch (InvalidTimeZoneException) { return TimeZoneLookup.Invalid; }
    }

    public static bool IsValid(string? id) => Lookup(id, out _) == TimeZoneLookup.Found;
}
