using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Servicedesk.Infrastructure.Triggers.Templating;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — the notify_agent trigger action writes bell rows only for
/// active Agent/Admin accounts, never for the person whose own step started
/// the pass, and anchors them on the form's note.
[Collection(PostgresCollection.Name)]
public sealed class NotifyAgentActionDbTests
{
    private readonly PostgresDatabase _db;

    public NotifyAgentActionDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Notifies_submitter_and_owner_but_not_the_actor_or_non_agents()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Licence request", await seed.ContactAsync(), q, s, p);
        var submitter = await seed.UserAsync("Agent");
        var owner = await seed.UserAsync("Agent");
        var actor = await seed.UserAsync("Agent");
        var customer = await seed.UserAsync("Customer");
        var inactive = await seed.UserAsync("Agent");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE users SET is_active = FALSE WHERE id = @inactive", new { inactive });
        var note = await seed.EventAsync(ticketId, "Note", isInternal: true, authorUserId: submitter);

        var ticket = TriggerBackofficeFormTests.MakeTicket(assignee: owner) with { Id = ticketId };
        var cs = TriggerChangeSet.ForForm(new TriggerFormEvent(
            "handled", Guid.NewGuid(), null, "Licence request", submitter, "Alex Example",
            actor, "Sam Example", "Done", AnchorEventId: note));
        var ctx = new TriggerEvaluationContext(ticketId, ticket, null, cs, DateTime.UtcNow);

        using var action = JsonDocument.Parse($$"""
            { "kind": "notify_agent", "targets": ["form_submitter", "ticket_owner"],
              "user_ids": ["{{actor}}", "{{customer}}", "{{inactive}}"], "message": "Form handled" }
            """);
        var handler = new NotifyAgentHandler(
            _db.DataSource, new NotificationRepository(_db.DataSource), new NullUserNotifier(),
            new TriggerTemplateRenderer(NullLogger<TriggerTemplateRenderer>.Instance),
            NullLogger<NotifyAgentHandler>.Instance);

        var result = await handler.ApplyAsync(action.RootElement, ctx, default);

        Assert.Equal(TriggerActionStatus.Applied, result.Status);
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        var rows = (await conn.QueryAsync<(Guid UserId, string Type, long EventId)>(
            "SELECT user_id, notification_type, event_id FROM user_notifications WHERE ticket_id = @ticketId",
            new { ticketId })).ToList();
        Assert.Equal(new[] { owner, submitter }.OrderBy(x => x), rows.Select(r => r.UserId).OrderBy(x => x));
        Assert.All(rows, r =>
        {
            Assert.Equal(NotifyAgentAction.NotificationType, r.Type);
            Assert.Equal(note, r.EventId);
        });
    }

    [PostgresFact]
    public async Task Without_an_anchor_event_it_is_a_no_op()
    {
        var ctx = new TriggerEvaluationContext(Guid.NewGuid(), TriggerBackofficeFormTests.MakeTicket(Guid.NewGuid()),
            null, TriggerChangeSet.Empty, DateTime.UtcNow);
        using var action = JsonDocument.Parse("""{ "kind": "notify_agent", "targets": ["ticket_owner"], "message": "x" }""");
        var handler = new NotifyAgentHandler(
            _db.DataSource, new NotificationRepository(_db.DataSource), new NullUserNotifier(),
            new TriggerTemplateRenderer(NullLogger<TriggerTemplateRenderer>.Instance),
            NullLogger<NotifyAgentHandler>.Instance);

        Assert.Equal(TriggerActionStatus.NoOp, (await handler.ApplyAsync(action.RootElement, ctx, default)).Status);
    }
}
