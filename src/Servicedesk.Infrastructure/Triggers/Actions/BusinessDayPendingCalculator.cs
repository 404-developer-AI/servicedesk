using Servicedesk.Domain.Sla;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Trigger-specific business-day arithmetic used by the
/// <c>set_pending_till</c> action when admins pick the
/// "businessDays + wakeAtLocal" mode (v0.0.24 patch).
/// Distinct from the SLA-engine's <c>BusinessHoursCalculator</c>:
/// that one walks **business minutes**; this one walks **business
/// days** and snaps the result to a fixed time-of-day in the
/// schedule's timezone — which is the natural mental model for
/// auto-bump triggers ("after 2 working days, wake at 08:00 local").
///
/// Algorithm:
///   1. Convert <paramref name="nowUtc"/> to local time in
///      <paramref name="schema"/>.Timezone.
///   2. Build candidate = today at <paramref name="wakeAtLocal"/>.
///      If that candidate is in the past, OR today isn't a business
///      day (weekend / holiday / no slots), move forward day-by-day
///      until candidate lands on a business day in the future. This
///      is "day 0" — the soonest wake-up moment.
///   3. Add <paramref name="businessDays"/> more business days,
///      each step = "advance to the next business day, same wake-time".
///   4. Convert local target back to UTC.
public static class BusinessDayPendingCalculator
{
    /// Maximum days the walk will advance. A misconfigured schema
    /// (every day a holiday, or zero slots configured for every day)
    /// would otherwise loop forever; 4 years is the same guard
    /// <see cref="Sla.BusinessHoursCalculator"/> uses.
    private const int WalkGuardDays = 366 * 4;

    public static DateTime Resolve(
        DateTime nowUtc,
        int businessDays,
        TimeSpan wakeAtLocal,
        BusinessHoursSchema schema)
    {
        if (businessDays < 0) throw new ArgumentOutOfRangeException(nameof(businessDays));
        if (wakeAtLocal < TimeSpan.Zero || wakeAtLocal >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(wakeAtLocal),
                "wakeAtLocal must be a time-of-day in [00:00, 24:00).");

        var tz = ResolveTimezone(schema.Timezone);
        var holidays = new HashSet<DateOnly>(schema.Holidays.Select(h => h.Date));
        var workingDays = schema.Slots.Select(s => s.DayOfWeek).ToHashSet();

        if (workingDays.Count == 0)
        {
            // No slots at all → degenerate schema. We bail loud rather
            // than silently waking the ticket immediately, because
            // setting pending-till to "now + 0" effectively skips the
            // pending state altogether and surprises the admin.
            throw new InvalidOperationException(
                "Business-hours schema has no working slots; cannot resolve a business-day wake-up.");
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        // 1) Day-0 candidate = today at wakeAtLocal. If that has already
        //    passed locally, jump forward one calendar day before the
        //    walk so we never return a moment in the past. Then advance
        //    to the next working day (today, if today is one). Net
        //    behaviour: Mon 18:00 → Tue 08:00; Tue 06:00 → Tue 08:00;
        //    Sat 06:00 → Mon 08:00.
        var candidate = localNow.Date.Add(wakeAtLocal);
        if (candidate <= localNow)
            candidate = candidate.AddDays(1);
        candidate = AdvanceToNextBusinessDay(candidate, workingDays, holidays);

        // 2) Advance one business day at a time for each requested step.
        for (var i = 0; i < businessDays; i++)
        {
            candidate = AdvanceToNextBusinessDay(
                candidate.AddDays(1), workingDays, holidays);
        }

        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }

    private static DateTime AdvanceToNextBusinessDay(
        DateTime candidateLocal,
        HashSet<int> workingDays,
        HashSet<DateOnly> holidays)
    {
        for (var i = 0; i < WalkGuardDays; i++)
        {
            if (IsBusinessDay(candidateLocal.Date, workingDays, holidays))
                return candidateLocal;
            candidateLocal = candidateLocal.AddDays(1);
        }
        throw new InvalidOperationException(
            "Business-day walk exceeded 4-year guard; schedule has no reachable working day.");
    }

    private static bool IsBusinessDay(
        DateTime localDate,
        HashSet<int> workingDays,
        HashSet<DateOnly> holidays)
    {
        if (holidays.Contains(DateOnly.FromDateTime(localDate))) return false;
        return workingDays.Contains((int)localDate.DayOfWeek);
    }

    private static TimeZoneInfo ResolveTimezone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
