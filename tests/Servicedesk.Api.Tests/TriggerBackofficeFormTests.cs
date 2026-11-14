using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.54 — back-office form events as trigger conditions. A form pass
/// carries the event on its change set; form triggers (any form.* field)
/// run only on such a pass and a form pass runs only form triggers, so an
/// "event is not handled" condition can never match on an ordinary note and
/// a handled form never re-fires every Always trigger on the ticket.
public sealed class TriggerBackofficeFormTests
{
    private static readonly TriggerConditionMatcher Matcher = new();
    private static readonly Guid TemplateId = Guid.Parse("2b9c4f1e-7d3a-4e8b-a1c6-5f0e9d2a7b31");

    private static TriggerFormEvent Form(string evt) => new(
        evt, Guid.NewGuid(), TemplateId, "Licence request", Guid.NewGuid(), "Alex Example",
        Guid.NewGuid(), "Sam Example", "Contract updated", AnchorEventId: 7);

    private static IReadOnlySet<string> Refs(string conditions)
    {
        using var doc = JsonDocument.Parse(conditions);
        return Matcher.ReferencedFields(doc.RootElement);
    }

    private const string HandledCondition = """
        { "op": "AND", "items": [
            { "field": "form.backoffice_event", "operator": "is", "value": "handled" },
            { "field": "form.template.id", "operator": "is", "value": "2b9c4f1e-7d3a-4e8b-a1c6-5f0e9d2a7b31" }
        ] }
        """;

    private const string TicketCondition = """
        { "op": "AND", "items": [ { "field": "ticket.is_callback", "operator": "is", "value": "true" } ] }
        """;

    [Fact]
    public void Form_triggers_run_only_on_form_passes_and_vice_versa()
    {
        var formPass = TriggerChangeSet.ForForm(Form("handled"));
        Assert.True(formPass.RunsTriggerReferencing(Refs(HandledCondition)));
        Assert.False(formPass.RunsTriggerReferencing(Refs(TicketCondition)));
        Assert.False(formPass.RunsTriggerReferencing(new HashSet<string>())); // an empty Always trigger

        var notePass = TriggerChangeSet.ArticleOnly();
        Assert.False(notePass.RunsTriggerReferencing(Refs(HandledCondition)));
        Assert.True(notePass.RunsTriggerReferencing(Refs(TicketCondition)));
        Assert.True(notePass.RunsTriggerReferencing(new HashSet<string>()));
    }

    [Fact]
    public void Form_pass_marks_the_form_fields_changed_for_selective_triggers()
    {
        // A Selective trigger with only a template condition must fire too.
        var changed = TriggerChangeSet.ForForm(Form("submitted")).ChangedFields;
        Assert.Contains(TriggerFieldKeys.FormBackofficeEvent, changed);
        Assert.Contains(TriggerFieldKeys.FormTemplateId, changed);
    }

    [Theory]
    [InlineData("handled", true)]
    [InlineData("submitted", false)]
    [InlineData("reopened", false)]
    public void Event_and_template_conditions_resolve_from_the_form_event(string evt, bool expected)
        => Assert.Equal(expected, Eval(HandledCondition, TriggerChangeSet.ForForm(Form(evt))));

    [Fact]
    public void Another_template_does_not_match()
    {
        var other = Form("handled") with { TemplateId = Guid.NewGuid() };
        Assert.False(Eval(HandledCondition, TriggerChangeSet.ForForm(other)));
    }

    [Fact]
    public void Merging_applied_changes_keeps_the_form_event()
    {
        // TriggerService folds applied field changes into the change set
        // between triggers; the form event must survive that.
        var cs = TriggerChangeSet.ForForm(Form("handled"));
        var merged = cs with { ChangedFields = new HashSet<string>(cs.ChangedFields) { TriggerFieldKeys.TicketStatusId } };
        Assert.NotNull(merged.FormEvent);
    }

    [Theory]
    [InlineData("""{ "kind": "notify_agent", "targets": ["form_submitter"], "message": "Handled: #{form.handled_note}" }""", null)]
    [InlineData("""{ "kind": "notify_agent", "user_ids": ["5d0c2b7a-8f14-4a3e-9c61-0b2e4f7a9d10"], "message": "x" }""", null)]
    [InlineData("""{ "kind": "notify_agent", "message": "x" }""", "choose at least one recipient.")]
    [InlineData("""{ "kind": "notify_agent", "targets": ["everyone"], "message": "x" }""", "unknown target")]
    [InlineData("""{ "kind": "notify_agent", "targets": ["ticket_owner"], "message": "  " }""", "requires a non-empty 'message'.")]
    [InlineData("""{ "kind": "notify_agent", "user_ids": ["not-a-guid"], "message": "x" }""", "'user_ids' must contain agent ids.")]
    public void Notify_agent_payload_is_validated(string action, string? expectedError)
    {
        using var doc = JsonDocument.Parse(action);
        var err = Servicedesk.Infrastructure.Triggers.Actions.NotifyAgentAction.Validate(doc.RootElement);
        if (expectedError is null) Assert.Null(err);
        else Assert.Contains(expectedError, err);
    }

    [Fact]
    public void Validator_accepts_a_form_trigger_with_notify_agent()
    {
        var result = TriggerValidator.Validate(
            "Notify technician when handled", "action", "selective", HandledCondition,
            """[ { "kind": "notify_agent", "targets": ["form_submitter"], "message": "#{form.name} handled by #{form.handled_by}" } ]""",
            locale: null, timezone: null);
        Assert.True(result.IsValid, result.Error);
    }

    private static bool Eval(string conditions, TriggerChangeSet cs)
    {
        var ctx = new TriggerEvaluationContext(Guid.NewGuid(), MakeTicket(), null, cs, DateTime.UtcNow);
        using var doc = JsonDocument.Parse(conditions);
        return Matcher.Matches(doc.RootElement, ctx);
    }

    internal static Ticket MakeTicket(Guid? assignee = null)
        => new(
            Id: Guid.NewGuid(), Number: 42, Subject: "Licence request",
            RequesterContactId: Guid.NewGuid(), AssigneeUserId: assignee,
            QueueId: Guid.NewGuid(), StatusId: Guid.NewGuid(), PriorityId: Guid.NewGuid(),
            CategoryId: null, Source: "Web", ExternalRef: null,
            CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow,
            DueUtc: null, FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null,
            IsDeleted: false);
}
