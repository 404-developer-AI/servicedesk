using Servicedesk.Domain.Sla;

namespace Servicedesk.Infrastructure.Sla;

public interface IBusinessHoursCalculator
{
    DateTime AddBusinessMinutes(DateTime startUtc, int minutes, BusinessHoursSchema schema);
    int BusinessMinutesBetween(DateTime fromUtc, DateTime toUtc, BusinessHoursSchema schema);
}

/// Computes business-time arithmetic against a schema of weekly slots + a holiday set.
/// All inputs/outputs are UTC; internal iteration is in the schema's timezone so DST
/// transitions fall out correctly. Holidays are whole days in the schema's local time.
public sealed class BusinessHoursCalculator : IBusinessHoursCalculator
{
    public DateTime AddBusinessMinutes(DateTime startUtc, int minutes, BusinessHoursSchema schema)
    {
        if (minutes <= 0) return DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);

        var tz = ResolveTimezone(schema.Timezone);
        var holidays = new HashSet<DateOnly>(schema.Holidays.Select(h => h.Date));
        var slotsByDay = GroupSlotsByDay(schema.Slots);

        if (slotsByDay.Count == 0)
        {
            return DateTime.SpecifyKind(startUtc.AddMinutes(minutes), DateTimeKind.Utc);
        }

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz);
        var remaining = minutes;
        var cursor = localStart;

        // Bound the loop so a misconfigured schema cannot wedge the worker.
        for (var guard = 0; guard < 366 * 4; guard++)
        {
            var day = DateOnly.FromDateTime(cursor);
            if (!holidays.Contains(day) && slotsByDay.TryGetValue((int)cursor.DayOfWeek, out var slots))
            {
                var minuteOfDay = cursor.Hour * 60 + cursor.Minute;
                foreach (var slot in slots)
                {
                    if (slot.EndMinute <= minuteOfDay) continue;
                    var slotStart = Math.Max(slot.StartMinute, minuteOfDay);
                    var slotEnd = slot.EndMinute;
                    var available = slotEnd - slotStart;
                    if (available <= 0) continue;

                    if (available >= remaining)
                    {
                        var deadlineLocal = cursor.Date.AddMinutes(slotStart + remaining);
                        return TimeZoneInfo.ConvertTimeToUtc(deadlineLocal, tz);
                    }
                    remaining -= available;
                    minuteOfDay = slotEnd;
                    cursor = cursor.Date.AddMinutes(slotEnd);
                }
            }
            cursor = cursor.Date.AddDays(1);
        }
        throw new InvalidOperationException("Business-hours addition exceeded 4-year guard; schema likely has no working hours.");
    }

    public int BusinessMinutesBetween(DateTime fromUtc, DateTime toUtc, BusinessHoursSchema schema)
    {
        if (toUtc <= fromUtc) return 0;
        var tz = ResolveTimezone(schema.Timezone);
        var holidays = new HashSet<DateOnly>(schema.Holidays.Select(h => h.Date));
        var slotsByDay = GroupSlotsByDay(schema.Slots);
        if (slotsByDay.Count == 0)
        {
            return (int)Math.Round((toUtc - fromUtc).TotalMinutes);
        }

        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), tz);
        var localTo = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc), tz);
        var total = 0;
        var cursor = localFrom.Date;
        while (cursor <= localTo.Date)
        {
            var day = DateOnly.FromDateTime(cursor);
            if (!holidays.Contains(day) && slotsByDay.TryGetValue((int)cursor.DayOfWeek, out var slots))
            {
                var lowerBound = cursor == localFrom.Date ? localFrom.Hour * 60 + localFrom.Minute : 0;
                var upperBound = cursor == localTo.Date ? localTo.Hour * 60 + localTo.Minute : 1440;
                foreach (var slot in slots)
                {
                    var s = Math.Max(slot.StartMinute, lowerBound);
                    var e = Math.Min(slot.EndMinute, upperBound);
                    if (e > s) total += e - s;
                }
            }
            cursor = cursor.AddDays(1);
        }
        return total;
    }

    private static TimeZoneInfo ResolveTimezone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static Dictionary<int, List<BusinessHoursSlot>> GroupSlotsByDay(IReadOnlyList<BusinessHoursSlot> slots)
    {
        var map = new Dictionary<int, List<BusinessHoursSlot>>();
        foreach (var slot in slots)
        {
            if (!map.TryGetValue(slot.DayOfWeek, out var list))
            {
                list = new List<BusinessHoursSlot>();
                map[slot.DayOfWeek] = list;
            }
            list.Add(slot);
        }
        foreach (var list in map.Values) list.Sort((a, b) => a.StartMinute.CompareTo(b.StartMinute));
        return map;
    }
}
