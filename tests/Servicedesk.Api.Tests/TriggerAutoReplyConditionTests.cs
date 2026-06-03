using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Guards the v0.0.57 fix: the default auto-reply must fire ONLY on the first
/// inbound customer mail (the one that opened the ticket), never on replies —
/// including a reply to our own auto-reply, which previously caused a loop.
public class TriggerAutoReplyConditionTests
{
    private static readonly TriggerConditionMatcher Matcher = new();

    // The seeded "010 - Auto-reply on new ticket" condition tree.
    private const string AutoReplyConditions = """
        { "op": "AND", "items": [
            { "field": "article.sender", "operator": "is", "value": "Customer" },
            { "field": "article.type", "operator": "is", "value": "MailReceived" },
            { "field": "ticket.is_new", "operator": "is", "value": "true" }
        ] }
        """;

    [Fact]
    public void Fires_on_first_inbound_customer_mail()
        => Assert.True(Eval(CustomerMail(), isTicketCreation: true));

    [Fact]
    public void Does_not_fire_on_a_reply_to_an_existing_ticket()
        => Assert.False(Eval(CustomerMail(), isTicketCreation: false));

    [Fact]
    public void Does_not_fire_on_agent_outbound_mail()
    {
        var agentMail = MakeEvent("MailSent", authorUserId: Guid.NewGuid(), authorContactId: null);
        Assert.False(Eval(agentMail, isTicketCreation: true));
    }

    [Fact]
    public void Ticket_is_new_resolves_from_the_creation_flag()
    {
        const string onlyIsNew = """
            { "op": "AND", "items": [
                { "field": "ticket.is_new", "operator": "is", "value": "true" }
            ] }
            """;
        Assert.True(EvalConditions(onlyIsNew, CustomerMail(), isTicketCreation: true));
        Assert.False(EvalConditions(onlyIsNew, CustomerMail(), isTicketCreation: false));
    }

    // ── helpers ──

    private static bool Eval(TicketEvent? evt, bool isTicketCreation)
        => EvalConditions(AutoReplyConditions, evt, isTicketCreation);

    private static bool EvalConditions(string conditions, TicketEvent? evt, bool isTicketCreation)
    {
        var ctx = new TriggerEvaluationContext(
            TicketId: Guid.NewGuid(),
            Ticket: MakeTicket(),
            TriggeringEvent: evt,
            ChangeSet: TriggerChangeSet.ArticleOnly(isTicketCreation: isTicketCreation),
            UtcNow: DateTime.UtcNow);
        using var doc = JsonDocument.Parse(conditions);
        return Matcher.Matches(doc.RootElement, ctx);
    }

    private static TicketEvent CustomerMail()
        => MakeEvent("MailReceived", authorUserId: null, authorContactId: Guid.NewGuid());

    private static TicketEvent MakeEvent(string type, Guid? authorUserId, Guid? authorContactId)
        => new(
            Id: 1, TicketId: Guid.NewGuid(), EventType: type,
            AuthorUserId: authorUserId, AuthorContactId: authorContactId, AuthorName: "X",
            BodyText: "help", BodyHtml: null, MetadataJson: "{}", IsInternal: false,
            CreatedUtc: DateTime.UtcNow, EditedUtc: null, EditedByUserId: null);

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
