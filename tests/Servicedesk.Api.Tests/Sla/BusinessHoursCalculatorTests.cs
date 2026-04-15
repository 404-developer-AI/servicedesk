using Servicedesk.Domain.Sla;
using Servicedesk.Infrastructure.Sla;
using Xunit;

namespace Servicedesk.Api.Tests.Sla;

public sealed class BusinessHoursCalculatorTests
{
    private static BusinessHoursSchema MondayToFridayNineToFive(params DateOnly[] holidays)
    {
        var schemaId = Guid.NewGuid();
        var slots = new List<BusinessHoursSlot>();
        for (var day = 1; day <= 5; day++)
        {
            slots.Add(new BusinessHoursSlot(day, schemaId, day, 540, 1020));
        }
        var holidayRecords = holidays.Select((d, i) => new Holiday(i + 1, schemaId, d, "Test holiday", "manual", "BE")).ToList();
        return new BusinessHoursSchema(schemaId, "MF 9-17", "Europe/Brussels", "BE", true, slots, holidayRecords);
    }

    [Fact]
    public void AddBusinessMinutes_WithinSingleSlot_RemainsSameDay()
    {
        var calc = new BusinessHoursCalculator();
        var schema = MondayToFridayNineToFive();
        // Monday 2026-03-02 09:30 UTC → Europe/Brussels 10:30 local (CET), add 120m → local 12:30
        var start = new DateTime(2026, 3, 2, 9, 30, 0, DateTimeKind.Utc);
        var result = calc.AddBusinessMinutes(start, 120, schema);
        Assert.Equal(new DateTime(2026, 3, 2, 11, 30, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void AddBusinessMinutes_CrossesWeekend()
    {
        var calc = new BusinessHoursCalculator();
        var schema = MondayToFridayNineToFive();
        // Friday 2026-03-06 16:00 local (15:00 UTC), add 120m (1h today + 1h Mon) → Mon 10:00 local
        var start = new DateTime(2026, 3, 6, 15, 0, 0, DateTimeKind.Utc);
        var result = calc.AddBusinessMinutes(start, 120, schema);
        Assert.Equal(new DateTime(2026, 3, 9, 9, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void AddBusinessMinutes_SkipsHoliday()
    {
        var calc = new BusinessHoursCalculator();
        var holiday = new DateOnly(2026, 4, 6); // Easter Monday 2026
        var schema = MondayToFridayNineToFive(holiday);
        // Fri 2026-04-03 16:00 local (14:00 UTC), add 180m → 1h Fri + 2h Tue
        var start = new DateTime(2026, 4, 3, 14, 0, 0, DateTimeKind.Utc);
        var result = calc.AddBusinessMinutes(start, 180, schema);
        Assert.Equal(new DateTime(2026, 4, 7, 9, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void BusinessMinutesBetween_CountsOnlyWorkingHours()
    {
        var calc = new BusinessHoursCalculator();
        var schema = MondayToFridayNineToFive();
        // Fri 2026-03-06 15:00 local → Mon 2026-03-09 11:00 local
        // Fri: 2h, Mon: 2h → 240 min
        var from = new DateTime(2026, 3, 6, 14, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 3, 9, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(240, calc.BusinessMinutesBetween(from, to, schema));
    }

    [Fact]
    public void BusinessMinutesBetween_ZeroWhenEndBeforeStart()
    {
        var calc = new BusinessHoursCalculator();
        var schema = MondayToFridayNineToFive();
        var from = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 3, 9, 11, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, calc.BusinessMinutesBetween(from, to, schema));
    }
}
