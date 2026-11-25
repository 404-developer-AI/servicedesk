using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.ComposeTemplates;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.67 — the pure Campaigns rules: token rendering (encoding, subject
/// line breaks, missing values, server zone + culture), eligibility, the
/// derived dashboard state and which Graph errors are retried.
public sealed class CampaignRulesTests
{
    private static readonly CampaignFormat Brussels =
        CampaignFormat.Create("Europe/Brussels", "dddd d MMMM yyyy", "HH:mm", "nl-BE");

    private static Dictionary<string, string> Tokens(string firstName = "Jane") => new(StringComparer.Ordinal)
    {
        ["{{contact.firstName}}"] = firstName,
        ["{{ticket.number}}"] = "1042",
    };

    [Fact]
    public void Body_values_are_html_encoded_and_the_subject_stays_one_line()
    {
        var r = CampaignRenderer.Render(
            "Update {{ticket.number}} for {{contact.firstName}}",
            "<p>Dear {{ contact.firstName }}</p>",
            "Firewalls", null,
            Tokens("<b>Jane</b>\r\nBcc: someone@example.test"), Brussels);

        Assert.Equal("<p>Dear &lt;b&gt;Jane&lt;/b&gt;\r\nBcc: someone@example.test</p>", r.BodyHtml);
        Assert.DoesNotContain('\n', r.Subject);
        Assert.DoesNotContain('\r', r.Subject);
        Assert.Empty(r.MissingTokens);
    }

    [Fact]
    public void A_token_without_a_value_stays_visible_and_is_reported()
    {
        var r = CampaignRenderer.Render("Update", "<p>Dear {{contact.firstName}}, {{campaign.proposedDate}}</p>",
            "Firewalls", null, Tokens(firstName: ""), Brussels);

        Assert.Contains("{{contact.firstName}}", r.BodyHtml);
        Assert.Equal(new[] { "{{contact.firstName}}", "{{campaign.proposedDate}}" }, r.MissingTokens);
    }

    [Fact]
    public void The_proposed_date_is_written_in_the_server_zone_and_culture()
    {
        // 2026-03-29 is the spring DST switch in Brussels: 08:00 UTC = 10:00 local.
        var proposed = new DateTime(2026, 3, 29, 8, 0, 0, DateTimeKind.Utc);

        var r = CampaignRenderer.Render("{{campaign.proposedTime}}", "<p>{{campaign.proposedDate}}</p>",
            "Firewalls", proposed, Tokens(), Brussels);

        Assert.Equal("10:00", r.Subject);
        Assert.Equal("<p>zondag 29 maart 2026</p>", r.BodyHtml);
    }

    [Fact]
    public void The_answer_link_is_an_encoded_anchor_in_the_body_and_a_url_in_the_subject()
    {
        var r = CampaignRenderer.Render("Answer: {{campaign.approvalLink}}", "<p>{{ campaign.approvalLink }}</p>",
            "Firewalls", null, Tokens(), Brussels,
            approvalUrl: "https://desk.example.test/campaign-answer/abc?x=1&y=2", approvalLabel: "Answer <online>");

        Assert.Equal("<p><a href=\"https://desk.example.test/campaign-answer/abc?x=1&amp;y=2\">Answer &lt;online&gt;</a></p>", r.BodyHtml);
        Assert.Equal("Answer: https://desk.example.test/campaign-answer/abc?x=1&y=2", r.Subject);
        Assert.Empty(r.MissingTokens);
        Assert.True(CampaignRenderer.UsesApprovalLink("", "<p>{{campaign.approvalLink}}</p>"));
        Assert.False(CampaignRenderer.UsesApprovalLink("Update", "<p>Hi</p>"));
    }

    [Fact]
    public void Without_a_link_the_answer_token_is_reported_missing()
    {
        var r = CampaignRenderer.Render("Update", "<p>{{campaign.approvalLink}}</p>", "Firewalls", null, Tokens(), Brussels);
        Assert.Equal(new[] { "{{campaign.approvalLink}}" }, r.MissingTokens);
    }

    [Fact]
    public void Link_tokens_round_trip_and_garbage_never_reaches_the_database()
    {
        var codec = new Servicedesk.Infrastructure.Security.LinkTokenCodec(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), "test.purpose");
        var (raw, hash, cipher) = codec.Mint();

        Assert.Equal(43, raw.Length);
        Assert.Equal(raw, codec.Reveal(cipher));
        Assert.Equal(hash, Servicedesk.Infrastructure.Security.LinkTokenCodec.HashForLookup(raw));
        Assert.Null(Servicedesk.Infrastructure.Security.LinkTokenCodec.HashForLookup("short"));
        Assert.Null(Servicedesk.Infrastructure.Security.LinkTokenCodec.HashForLookup("../../etc/passwd/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Null(Servicedesk.Infrastructure.Security.LinkTokenCodec.HashForLookup(null));
        Assert.Null(new Servicedesk.Infrastructure.Security.LinkTokenCodec(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), "other").Reveal(cipher));
    }

    [Fact]
    public void The_survey_invite_renderer_encodes_contact_names_too()
    {
        var map = new Dictionary<string, string> { ["{{contact.firstName}}"] = "<img src=x onerror=alert(1)>" };

        var body = ComposeTokenRenderer.Render("<p>Hi {{contact.firstName}}</p>", map, ComposeTokenEscape.Html);

        Assert.Equal("<p>Hi &lt;img src=x onerror=alert(1)&gt;</p>", body.Output);
    }

    [Theory]
    [InlineData(true, false, "Open", "jane@example.test", CampaignSkipReasons.ProjectTicket)]
    [InlineData(false, true, "Open", "jane@example.test", CampaignSkipReasons.Merged)]
    [InlineData(false, false, "Closed", "jane@example.test", CampaignSkipReasons.Closed)]
    [InlineData(false, false, "Open", "", CampaignSkipReasons.NoRequesterEmail)]
    [InlineData(false, false, "Resolved", "jane@example.test", null)]
    [InlineData(false, false, "Pending", "jane@example.test", null)]
    public void Eligibility_follows_the_single_ticket_mail_rules(
        bool projectBlocked, bool merged, string category, string email, string? expected)
    {
        Assert.Equal(expected, CampaignEligibility.Check(projectBlocked, merged, category, email));
    }

    [Fact]
    public void Missing_tokens_make_a_ticket_ineligible()
    {
        Assert.Equal(CampaignSkipReasons.MissingTokens,
            CampaignEligibility.Check(false, false, "Open", "jane@example.test", new[] { "{{contact.firstName}}" }));
    }

    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Dashboard_state_is_derived_in_priority_order()
    {
        Assert.Equal(CampaignRecipientStates.NotSent, Derive(new CampaignRecipientRow()));
        Assert.Equal(CampaignRecipientStates.Queued, Derive(new CampaignRecipientRow { MessageState = "Queued" }));
        Assert.Equal(CampaignRecipientStates.Failed, Derive(new CampaignRecipientRow { MessageState = "Failed" }));
        Assert.Equal(CampaignRecipientStates.Skipped, Derive(new CampaignRecipientRow { MessageState = "Skipped" }));
        Assert.Equal(CampaignRecipientStates.Waiting, Derive(Sent()));
        Assert.Equal(CampaignRecipientStates.ReplyReceived, Derive(Sent(reply: Now.AddHours(-1))));
        Assert.Equal(CampaignRecipientStates.TimerExpired, Derive(Sent(pendingTill: Now.AddHours(-1))));
        // An answer wins over an expired timer and over a later failed follow-up.
        Assert.Equal(CampaignRecipientStates.ReplyReceived,
            Derive(Sent(reply: Now.AddHours(-2), pendingTill: Now.AddHours(-1), state: "Failed")));
    }

    private static string Derive(CampaignRecipientRow r) => CampaignRecipientState.Derive(r, Now);

    private static CampaignRecipientRow Sent(DateTime? reply = null, DateTime? pendingTill = null, string state = "Sent") => new()
    {
        MessageState = state,
        FirstSentUtc = Now.AddDays(-3),
        LastSentUtc = Now.AddDays(-3),
        ReplyUtc = reply,
        RoundPendingTillUtc = pendingTill,
    };

    [Fact]
    public void Throttling_server_errors_and_network_trouble_are_retried_other_refusals_are_not()
    {
        Assert.True(CampaignSendService.IsTransient(new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 429 }));
        Assert.True(CampaignSendService.IsTransient(new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 503 }));
        Assert.True(CampaignSendService.IsTransient(new HttpRequestException("reset")));
        Assert.False(CampaignSendService.IsTransient(new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 403 }));
        Assert.False(CampaignSendService.IsTransient(new InvalidOperationException("no draft id")));
    }
}
