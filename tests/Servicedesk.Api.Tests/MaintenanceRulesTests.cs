using Servicedesk.Infrastructure.Maintenance;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.67 (phase 3) — the pure maintenance planner rules: the fixed
/// calendar (anchor day across short months) and the intervention moment in
/// the server zone.
public sealed class MaintenanceRulesTests
{
    [Theory]
    [InlineData("2026-08-31", 6, 31, "2027-02-28")]
    [InlineData("2027-02-28", 6, 31, "2027-08-31")]
    [InlineData("2028-08-31", 6, 31, "2029-02-28")]
    [InlineData("2026-01-15", 12, 15, "2027-01-15")]
    [InlineData("2026-03-30", 1, 30, "2026-04-30")]
    public void The_calendar_keeps_the_anchor_day(string due, int months, int anchor, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), MaintenanceCalendar.NextDue(DateTime.Parse(due), months, anchor));
    }

    [Fact]
    public void The_intervention_moment_is_the_date_at_the_plans_time_in_the_server_zone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels");
        Assert.Equal(new DateTime(2026, 1, 12, 8, 0, 0), MaintenanceCalendar.ProposedUtc(new DateTime(2026, 1, 12), 540, zone));
        Assert.Equal(new DateTime(2026, 7, 13, 7, 0, 0), MaintenanceCalendar.ProposedUtc(new DateTime(2026, 7, 13), 540, zone));
        // 02:30 does not exist on the spring DST night: it moves an hour on.
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0), MaintenanceCalendar.ProposedUtc(new DateTime(2026, 3, 29), 150, zone));
    }

    [Theory]
    [InlineData("{{plan.name}} — {{member.label}}", "", "{{plan.name}}")]
    [InlineData("{{plan.name}} - {{member.label}} ({{contact.firstName}})", "", "{{plan.name}} ({{contact.firstName}})")]
    [InlineData("{{plan.name}} — {{member.label}}", "HQ", "{{plan.name}} — {{member.label}}")]
    public void An_empty_label_drops_its_placeholder_and_separator(string template, string label, string expected)
    {
        Assert.Equal(expected, MaintenanceTokens.DropEmptyLabel(template, label));
    }

    [Fact]
    public void Pending_lasts_the_plans_days_but_never_past_the_intervention()
    {
        var now = new DateTime(2026, 11, 17, 9, 0, 0);
        var intervention = new DateTime(2026, 12, 1, 8, 0, 0);
        Assert.Null(MaintenanceCalendar.PendingTill(false, 2, intervention, now));
        Assert.Equal(intervention, MaintenanceCalendar.PendingTill(true, null, intervention, now));
        Assert.Equal(now.AddDays(2), MaintenanceCalendar.PendingTill(true, 2, intervention, now));
        Assert.Equal(intervention, MaintenanceCalendar.PendingTill(true, 30, intervention, now));
        Assert.Null(MaintenanceCalendar.PendingTill(true, 2, intervention, intervention.AddMinutes(1)));
    }
}
