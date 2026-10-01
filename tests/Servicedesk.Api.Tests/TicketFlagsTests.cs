using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.17 — Call-back / Research ticket flags: trigger conditions resolve
/// off the ticket, a PATCH that toggles a flag lands in the trigger change
/// set, and the colour settings only accept #rrggbb (they are painted into
/// inline styles in every agent's browser).
public class TicketFlagsTests
{
    private static readonly TriggerConditionMatcher Matcher = new();

    [Theory]
    [InlineData("ticket.is_callback", true, false, "true", true)]
    [InlineData("ticket.is_callback", false, false, "true", false)]
    [InlineData("ticket.is_callback", false, false, "false", true)]
    [InlineData("ticket.is_research", false, true, "true", true)]
    [InlineData("ticket.is_research", true, false, "true", false)]
    public void Flag_conditions_resolve_from_the_ticket(
        string field, bool isCallback, bool isResearch, string expected, bool matches)
    {
        var conditions = $$"""
            { "op": "AND", "items": [
                { "field": "{{field}}", "operator": "is", "value": "{{expected}}" }
            ] }
            """;
        var ctx = new TriggerEvaluationContext(
            TicketId: Guid.NewGuid(),
            Ticket: MakeTicket() with { IsCallback = isCallback, IsResearch = isResearch },
            TriggeringEvent: null,
            ChangeSet: TriggerChangeSet.Empty,
            UtcNow: DateTime.UtcNow);
        using var doc = JsonDocument.Parse(conditions);
        Assert.Equal(matches, Matcher.Matches(doc.RootElement, ctx));
    }

    [Fact]
    public void Flag_conditions_are_in_the_editor_catalog()
    {
        var keys = TriggerConditionFieldCatalog.All.Where(f => f.Type == "boolean").Select(f => f.Key).ToList();
        Assert.Contains(TriggerFieldKeys.TicketIsCallback, keys);
        Assert.Contains(TriggerFieldKeys.TicketIsResearch, keys);
    }

    [Fact]
    public void Toggling_a_flag_marks_it_changed_for_selective_triggers()
    {
        var changed = TicketMutationService.DeriveChangedFields(new TicketFieldUpdate(IsCallback: false));
        Assert.Contains(TriggerFieldKeys.TicketIsCallback, changed);
        Assert.DoesNotContain(TriggerFieldKeys.TicketIsResearch, changed);

        var none = TicketMutationService.DeriveChangedFields(new TicketFieldUpdate(PriorityId: Guid.NewGuid()));
        Assert.DoesNotContain(TriggerFieldKeys.TicketIsCallback, none);
    }

    [Theory]
    [InlineData("#22c55e", true)]
    [InlineData("#3B82F6", true)]
    [InlineData("22c55e", false)]
    [InlineData("#fff", false)]
    [InlineData("red", false)]
    [InlineData("#22c55e;background:url(x)", false)]
    [InlineData("", false)]
    public void Colour_settings_only_accept_hex(string value, bool ok)
    {
        foreach (var key in new[] { SettingKeys.Tickets.CallbackColor, SettingKeys.Tickets.ResearchColor })
        {
            var def = SettingDefaults.All.Single(d => d.Key == key);
            Assert.Null(SettingValueValidator.Validate(def, def.Value));
            Assert.Equal(ok, SettingValueValidator.Validate(def, value) is null);
        }
    }

    [Fact]
    public void Flag_settings_have_the_agreed_defaults()
    {
        Assert.Equal("#22c55e", SettingDefaults.All.Single(d => d.Key == SettingKeys.Tickets.CallbackColor).Value);
        Assert.Equal("#3b82f6", SettingDefaults.All.Single(d => d.Key == SettingKeys.Tickets.ResearchColor).Value);
        // Off by default since the Call composer clears the flag (v0.1.17).
        Assert.Equal("false", SettingDefaults.All.Single(d => d.Key == SettingKeys.Tickets.CallbackOpenPromptEnabled).Value);
        Assert.Equal("true", SettingDefaults.All.Single(d => d.Key == SettingKeys.Tickets.CallbackClearOnCall).Value);
    }

    [Fact]
    public void A_logged_call_counts_as_first_contact_by_default()
    {
        var def = SettingDefaults.All.Single(d => d.Key == SettingKeys.Sla.FirstContactTriggers);
        var triggers = JsonSerializer.Deserialize<string[]>(def.Value)!;
        Assert.Contains("Call", triggers);
    }

    [Fact]
    public void Customer_visible_calls_pass_the_portal_allow_list()
    {
        // Internal calls are still dropped by the is_internal filter.
        Assert.Contains("Call", Servicedesk.Infrastructure.Portal.PortalTicketRepository.CustomerVisibleEventTypes);
        Assert.DoesNotContain("TicketFlagChange", Servicedesk.Infrastructure.Portal.PortalTicketRepository.CustomerVisibleEventTypes);
    }

    private static Ticket MakeTicket()
        => new(
            Id: Guid.NewGuid(), Number: 42, Subject: "Call me back",
            RequesterContactId: Guid.NewGuid(), AssigneeUserId: null,
            QueueId: Guid.NewGuid(), StatusId: Guid.NewGuid(), PriorityId: Guid.NewGuid(),
            CategoryId: null, Source: "Web", ExternalRef: null,
            CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow,
            DueUtc: null, FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null,
            IsDeleted: false);
}
