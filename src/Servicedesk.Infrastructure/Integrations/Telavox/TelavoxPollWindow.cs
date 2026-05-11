namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Pure helpers for evaluating the working-hours polling window. Extracted
/// from the worker so the time-arithmetic (midnight-wrap, day-mask, malformed
/// input) can be unit-tested without spinning up a BackgroundService.
public static class TelavoxPollWindow
{
    /// Workweek default Mon..Sun. Returned only when the setting row is
    /// empty (no admin input at all). Malformed input fails OPEN (all-days)
    /// instead — see <see cref="ParseDayMask"/> for the rationale.
    private static readonly bool[] DefaultMask = { true, true, true, true, true, false, false };

    /// Fail-open mask returned when the setting value is non-empty but
    /// malformed. Mirrors the time-of-day parser: a typo must not silently
    /// disable the integration; the all-days fallback keeps the poll
    /// attempting and the integration_audit log surfaces the situation.
    private static readonly bool[] OpenMask = { true, true, true, true, true, true, true };

    /// Returns true when <paramref name="nowLocal"/> falls inside the
    /// configured working-hours window. Inputs are the raw setting strings
    /// so the worker can pass them straight through without parsing
    /// upfront. Empty / malformed values fail OPEN (treat as always-on) —
    /// an admin's typo should not silently disable the integration, the
    /// poll-attempts will surface in integration_audit either way.
    public static bool IsInPollWindow(
        DateTime nowLocal,
        string startRaw,
        string endRaw,
        string daysRaw)
    {
        var mask = ParseDayMask(daysRaw);
        if (!mask[MaskIndexForDay(nowLocal.DayOfWeek)]) return false;

        var start = ParseTimeOfDay(startRaw);
        var end = ParseTimeOfDay(endRaw);
        if (start is null || end is null) return true; // fail-open

        var startMin = start.Value.Hour * 60 + start.Value.Minute;
        var endMin = end.Value.Hour * 60 + end.Value.Minute;
        var nowMin = nowLocal.Hour * 60 + nowLocal.Minute;

        // Start == End is the "window disabled" sentinel from the setting docs.
        if (startMin == endMin) return true;
        if (startMin < endMin) return nowMin >= startMin && nowMin < endMin;
        // Window wraps midnight (e.g. 22:00 → 06:00 night-shift).
        return nowMin >= startMin || nowMin < endMin;
    }

    internal static bool[] ParseDayMask(string raw)
    {
        // Empty = no admin input → workweek default (matches the seeded
        // value). Malformed (wrong arity or non-bool token) = fail-open
        // (all days on) so the integration keeps polling and the
        // misconfiguration is visible in integration_audit rather than
        // silently weekend-only. This mirrors the fail-open behaviour of
        // ParseTimeOfDay so admins see consistent rules across both
        // window-setting halves.
        if (string.IsNullOrWhiteSpace(raw)) return DefaultMask;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 7) return OpenMask;
        var result = new bool[7];
        for (var i = 0; i < 7; i++)
        {
            if (!bool.TryParse(parts[i], out var v)) return OpenMask;
            result[i] = v;
        }
        return result;
    }

    internal static (int Hour, int Minute)? ParseTimeOfDay(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Trim().Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || h < 0 || h > 23) return null;
        if (!int.TryParse(parts[1], out var m) || m < 0 || m > 59) return null;
        return (h, m);
    }

    /// Maps <see cref="DayOfWeek"/> to its slot in the Mon..Sun array the
    /// setting stores. .NET's enum starts at Sunday=0; this normalises to
    /// the Monday-first ordering admins expect.
    internal static int MaskIndexForDay(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 5,
        DayOfWeek.Sunday => 6,
        _ => 0,
    };
}
