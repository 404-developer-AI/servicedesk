using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Guards the v0.0.92 hard loop-prevention gate: a trigger fired by an
/// auto-submitted inbound mail (out-of-office, bounce, autoresponder — flagged
/// at ingest) must NEVER send mail toward the customer, no matter how the
/// trigger is configured. The gate is code, not configuration, by design.
public sealed class SendMailHandlerLoopGateTests
{
    private const string AutoFrom = "autoresponder@customer.example";

    // ── end-to-end suppression through ApplyAsync ──
    // The gate sits before every dependency call, so the handler can be
    // constructed with null collaborators: reaching any of them would throw,
    // which is exactly what these tests prove does not happen.

    [Fact]
    public async Task Customer_mail_is_suppressed_when_triggering_article_is_auto_submitted()
    {
        var result = await ApplyAsync(toSpec: "customer", AutoReplyEvent());

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Contains("loop prevention", JsonSerializer.Serialize(result.ChangeSummary));
    }

    [Fact]
    public async Task Explicit_address_matching_the_auto_sender_is_suppressed()
    {
        var result = await ApplyAsync(toSpec: $"address:{AutoFrom}", AutoReplyEvent());

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Contains("loop prevention", JsonSerializer.Serialize(result.ChangeSummary));
    }

    // ── classification precision (no over-suppression) ──

    [Fact]
    public void Inbound_mail_with_auto_reply_flag_classifies_and_exposes_sender()
    {
        var detected = SendMailHandler.IsAutoSubmittedArticle(AutoReplyEvent(), out var from);

        Assert.True(detected);
        Assert.Equal(AutoFrom, from);
    }

    [Fact]
    public void Inbound_mail_without_flag_does_not_classify()
        => Assert.False(SendMailHandler.IsAutoSubmittedArticle(
            MakeEvent("MailReceived", $$"""{"from":"{{AutoFrom}}"}"""), out _));

    [Fact]
    public void Outbound_mail_never_classifies_even_with_flag_in_metadata()
        => Assert.False(SendMailHandler.IsAutoSubmittedArticle(
            MakeEvent("MailSent", """{"auto_reply":true}"""), out _));

    [Fact]
    public void Null_event_and_malformed_metadata_do_not_classify()
    {
        Assert.False(SendMailHandler.IsAutoSubmittedArticle(null, out _));
        Assert.False(SendMailHandler.IsAutoSubmittedArticle(
            MakeEvent("MailReceived", "not-json{"), out _));
        Assert.False(SendMailHandler.IsAutoSubmittedArticle(
            MakeEvent("MailReceived", """{"auto_reply":"yes"}"""), out _));
    }

    // ── helpers ──

    private static async Task<TriggerActionResult> ApplyAsync(string toSpec, TicketEvent triggeringEvent)
    {
        var handler = new SendMailHandler(
            graph: null!, taxonomy: null!, tickets: null!, mail: null!,
            companies: null!, users: null!, queueAccess: null!, settings: null!,
            sla: null!, dedup: null!, renderer: null!, signatures: null!,
            logger: NullLogger<SendMailHandler>.Instance);

        using var action = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            to = toSpec,
            subject = "Re: your ticket",
            body_html = "<p>We received your message.</p>",
        }));

        var ctx = new TriggerEvaluationContext(
            TicketId: Guid.NewGuid(),
            Ticket: MakeTicket(),
            TriggeringEvent: triggeringEvent,
            ChangeSet: TriggerChangeSet.ArticleOnly(isTicketCreation: false),
            UtcNow: DateTime.UtcNow,
            TriggerId: Guid.NewGuid());

        return await handler.ApplyAsync(action.RootElement, ctx, default);
    }

    private static TicketEvent AutoReplyEvent()
        => MakeEvent("MailReceived", $$"""
            {"from":"{{AutoFrom}}","auto_reply":true,"auto_reply_signal":"Auto-Submitted: auto-replied"}
            """);

    private static TicketEvent MakeEvent(string type, string metadataJson)
        => new(
            Id: 1, TicketId: Guid.NewGuid(), EventType: type,
            AuthorUserId: null, AuthorContactId: Guid.NewGuid(), AuthorName: "X",
            BodyText: "I am out of office", BodyHtml: null, MetadataJson: metadataJson,
            IsInternal: false, CreatedUtc: DateTime.UtcNow, EditedUtc: null, EditedByUserId: null);

    private static Ticket MakeTicket()
        => new(
            Id: Guid.NewGuid(), Number: 42, Subject: "Printer broken",
            RequesterContactId: Guid.NewGuid(), AssigneeUserId: null,
            QueueId: Guid.NewGuid(), StatusId: Guid.NewGuid(), PriorityId: Guid.NewGuid(),
            CategoryId: null, Source: "Mail", ExternalRef: null,
            CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow,
            DueUtc: null, FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null,
            IsDeleted: false);
}
