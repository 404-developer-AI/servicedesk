using System.Text.Json;
using Servicedesk.Infrastructure.Insights.Rewind;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Servicedesk.Infrastructure.Workflow;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.32 — Insights Workflow step 1: template detection from content,
/// the agent stamp on request-path trigger events, and pickup positions.
public sealed class WorkflowCaptureTests
{
    private static readonly Guid CallTemplate = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid MailTemplate = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");

    private const string NoteOfCall = """
        <p>Hello {{contact.firstName}},</p>
        <p><strong>Wat heeft de klant aangegeven?</strong></p><ul><li><p></p></li></ul>
        <p><strong>Interne acties / troubleshooting?</strong></p><ul><li><p></p></li></ul>
        <p><strong>Next Steps?</strong></p><ul><li><p></p></li></ul>
        """;

    private static readonly IReadOnlyList<WorkflowTemplateCandidate> Candidates = new[]
    {
        new WorkflowTemplateCandidate(CallTemplate, NoteOfCall),
        new WorkflowTemplateCandidate(MailTemplate, "<p>Kind regards, the service desk team</p>"),
    };

    private static readonly IReadOnlyDictionary<string, string> Tokens =
        new Dictionary<string, string> { ["{{contact.firstName}}"] = "Alex" };

    // ---- template detection ----------------------------------------------

    [Fact]
    public void Untouched_template_is_recognised_but_not_filled()
    {
        var inserted = WorkflowTemplateDetector.Substitute(NoteOfCall, Tokens);

        var match = WorkflowTemplateDetector.Detect(inserted, Candidates, Tokens);

        Assert.Equal(CallTemplate, match!.TemplateId);
        Assert.False(match.Filled); // resolved name is template, not agent input
    }

    [Fact]
    public void Template_with_typed_answers_is_filled()
    {
        var body = WorkflowTemplateDetector.Substitute(NoteOfCall, Tokens)
            .Replace("<ul><li><p></p></li></ul>", "<ul><li><p>Printer offline since this morning</p></li></ul>");

        var match = WorkflowTemplateDetector.Detect(body, Candidates, Tokens);

        Assert.Equal(CallTemplate, match!.TemplateId);
        Assert.True(match.Filled);
    }

    [Fact]
    public void Unresolved_token_left_raw_is_not_counted_as_input()
    {
        // No token values → the raw {{contact.firstName}} stays, as on the client.
        var inserted = WorkflowTemplateDetector.Substitute(NoteOfCall, null);

        var match = WorkflowTemplateDetector.Detect(inserted, Candidates, null);

        Assert.False(match!.Filled);
    }

    [Fact]
    public void Ordinary_note_matches_no_template()
    {
        var match = WorkflowTemplateDetector.Detect(
            "<p>Called the customer, printer works again after a restart.</p>", Candidates, Tokens);

        Assert.Null(match);
    }

    [Fact]
    public void Heavily_rewritten_template_no_longer_counts()
    {
        var match = WorkflowTemplateDetector.Detect(
            "<p>Wat heeft de klant aangegeven? Printer offline.</p>", Candidates, Tokens);

        Assert.Null(match); // under 90% of the template's own words
    }

    [Fact]
    public void Templates_with_too_little_static_text_are_never_matched()
    {
        var tiny = new[] { new WorkflowTemplateCandidate(CallTemplate, "<p>{{contact.firstName}} ok</p>") };

        Assert.Null(WorkflowTemplateDetector.Detect("<p>Alex ok, all done</p>", tiny, Tokens));
    }

    // ---- trigger origin ---------------------------------------------------

    [Fact]
    public void Trigger_events_carry_the_agent_only_inside_a_request_scope()
    {
        var trigger = Guid.NewGuid();
        var agent = Guid.NewGuid();

        var outside = JsonDocument.Parse(TriggerEventMetadata.FieldChange(null, Guid.NewGuid(), null, "Pending", trigger));
        string inside;
        using (TriggerOrigin.Agent(agent))
            inside = TriggerEventMetadata.FieldChange(null, Guid.NewGuid(), null, "Pending", trigger);
        var after = JsonDocument.Parse(TriggerEventMetadata.SystemNote(trigger));

        Assert.False(outside.RootElement.TryGetProperty("on_behalf_of", out _));
        Assert.Equal(agent, JsonDocument.Parse(inside).RootElement.GetProperty("on_behalf_of").GetGuid());
        Assert.False(after.RootElement.TryGetProperty("on_behalf_of", out _));
        Assert.Null(TriggerOrigin.OnBehalfOfUserId);
    }

    // ---- pickup position --------------------------------------------------

    [Fact]
    public void Pickup_records_position_and_the_tickets_above_in_its_group()
    {
        var a = Item(1);
        var b = Item(2);
        var c = Item(3);
        var groups = new[]
        {
            new RewindArrangedGroup(RewindArranger.KeyCallback, "Call-back", null, new[] { a }),
            new RewindArrangedGroup("wfp", "Waiting for pickup", null, new[] { b, c }),
        };

        var hit = WorkflowCaptureService.Locate(groups, c.Id);

        Assert.Equal("wfp", hit!.Value.Group.Key);
        Assert.Equal(2, hit.Value.Position);
        Assert.Equal(new[] { b.Id }, hit.Value.Above);
        Assert.Null(WorkflowCaptureService.Locate(groups, Guid.NewGuid()));
    }

    private static TicketListItem Item(long n) => new(
        Id: new Guid($"dddddddd-0000-0000-0000-{n:D12}"), Number: n, Subject: $"Ticket {n}",
        QueueId: Guid.Empty, QueueName: "Servicedesk",
        StatusId: Guid.Empty, StatusName: "Open", StatusColor: "#888", StatusStateCategory: "Open",
        PriorityId: Guid.Empty, PriorityName: "Normal", PriorityLevel: 2, PriorityColor: "#999", PriorityIsDefault: true,
        RequesterContactId: Guid.Empty, RequesterEmail: "requester@example.test",
        RequesterFirstName: "Alex", RequesterLastName: "Doe", RequesterCompanyId: null, CompanyName: null,
        AssigneeUserId: null, AssigneeEmail: null, CategoryId: null, CategoryName: null,
        CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow, DueUtc: null, PendingTillUtc: null);
}
