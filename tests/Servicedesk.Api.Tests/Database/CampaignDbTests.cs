using Dapper;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Campaigns;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.67 — Campaigns on real SQL: recipients are queue-scoped (with a
/// hidden count), "reply received" is derived from customer mail after the
/// first send (auto-replies ignored), and the outbox never hands one row to
/// two workers, never enqueues a ticket twice per round and never resends an
/// interrupted mail on its own.
[Collection(PostgresCollection.Name)]
public sealed class CampaignDbTests
{
    private readonly PostgresDatabase _db;

    public CampaignDbTests(PostgresDatabase db) => _db = db;

    private sealed record World(Guid Campaign, Guid QueueA, Guid QueueB, Guid TicketA, Guid TicketB, Guid Sender, Guid Status, Guid Priority);

    private async Task<World> SeedAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queueA, status, priority) = await seed.TaxonomyAsync();
        var queueB = await seed.QueueAsync("Other");
        var ticketA = await seed.TicketAsync("Firewall A", await seed.ContactAsync(), queueA, status, priority);
        var ticketB = await seed.TicketAsync("Firewall B", await seed.ContactAsync(), queueB, status, priority);
        var sender = await seed.UserAsync("Admin");
        var campaigns = new CampaignRepository(_db.DataSource);
        var campaign = await campaigns.CreateAsync(new NewCampaign(
            "Firewall updates", "Update {{ticket.number}}", "<p>Dear {{contact.firstName}}</p>",
            null, null, null, sender), default);
        var recipients = new CampaignRecipientRepository(_db.DataSource);
        await recipients.AddAsync(campaign, new[] { ticketA, ticketB }, CampaignTicketSources.Selection,
            sender, QueueAccessScope.AdminScope, 500, default);
        return new World(campaign, queueA, queueB, ticketA, ticketB, sender, status, priority);
    }

    private static QueueAccessScope Only(Guid queue) => new(false, new HashSet<Guid> { queue });

    private async Task<Guid> EnqueueAsync(World w, params Guid[] tickets)
    {
        var outbox = new CampaignOutboxRepository(_db.DataSource);
        var (roundId, _) = await outbox.EnqueueRoundAsync(
            new NewCampaignRound(w.Campaign, CampaignRoundKinds.Initial, "S", "<p>B</p>", w.Sender, "Admin", null, null),
            tickets.Select(t => new NewCampaignMessage(t, CampaignMessageStates.Queued, null, "jane@example.test", "S", "<p>B</p>")).ToList(),
            default);
        return roundId;
    }

    private async Task MarkAllSentAsync(Guid campaign, DateTime sentUtc)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE campaign_messages SET state = 'Sent', sent_utc = @sentUtc WHERE campaign_id = @campaign",
            new { campaign, sentUtc });
    }

    private async Task InboundAsync(Guid ticketId, DateTime at, string type = "MailReceived", bool autoSubmitted = false)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        var eventId = await c.ExecuteScalarAsync<long>("""
            INSERT INTO ticket_events (ticket_id, event_type, body_text, is_internal, created_utc)
            VALUES (@ticketId, @type, 'Fine by us', FALSE, @at) RETURNING id
            """, new { ticketId, type, at });
        if (type == "MailReceived")
        {
            await c.ExecuteAsync("""
                INSERT INTO mail_messages (message_id, from_address, mailbox_address, received_utc, ticket_id,
                                           ticket_event_id, is_auto_submitted)
                VALUES (@messageId, 'jane@example.test', 'support@example.test', @at, @ticketId, @eventId, @autoSubmitted)
                """, new { messageId = $"<{Guid.NewGuid()}@example.test>", at, ticketId, eventId, autoSubmitted });
        }
    }

    [PostgresFact]
    public async Task Recipients_outside_the_callers_queues_are_hidden_and_counted()
    {
        var w = await SeedAsync();
        var reader = new CampaignDashboardReader(_db.DataSource);

        var board = await reader.ReadAsync(w.Campaign, Only(w.QueueA), DateTime.UtcNow, default);

        Assert.Equal(new[] { w.TicketA }, board.Recipients.Select(r => r.Row.TicketId).ToArray());
        Assert.Equal(1, board.HiddenCount);
    }

    [PostgresFact]
    public async Task Adding_tickets_respects_queue_access_and_skips_project_tickets()
    {
        var w = await SeedAsync();
        var seed = new DbSeed(_db.DataSource);
        var project = await seed.TicketAsync("Project", await seed.ContactAsync(), w.QueueA, w.Status, w.Priority, isProject: true);
        var other = await seed.TicketAsync("Firewall C", await seed.ContactAsync(), w.QueueB, w.Status, w.Priority);
        var fresh = await new CampaignRepository(_db.DataSource).CreateAsync(
            new NewCampaign("Second", "", "", null, null, null, w.Sender), default);

        var result = await new CampaignRecipientRepository(_db.DataSource).AddAsync(
            fresh, new[] { w.TicketA, project, other }, CampaignTicketSources.Selection, w.Sender, Only(w.QueueA), 500, default);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.NotAllowed);
    }

    [PostgresFact]
    public async Task A_customer_mail_after_the_send_is_a_reply_an_auto_reply_or_earlier_mail_is_not()
    {
        var w = await SeedAsync();
        await EnqueueAsync(w, w.TicketA, w.TicketB);
        var sent = DateTime.UtcNow.AddMinutes(-30);
        await MarkAllSentAsync(w.Campaign, sent);
        await InboundAsync(w.TicketA, sent.AddMinutes(-5));                      // before the send
        await InboundAsync(w.TicketA, sent.AddMinutes(5), autoSubmitted: true);  // out-of-office
        await InboundAsync(w.TicketB, sent.AddMinutes(10), "PortalMessage");     // a real answer

        var board = await new CampaignDashboardReader(_db.DataSource)
            .ReadAsync(w.Campaign, QueueAccessScope.AdminScope, DateTime.UtcNow, default);

        var state = board.Recipients.ToDictionary(r => r.Row.TicketId, r => r.State);
        Assert.Equal(CampaignRecipientStates.Waiting, state[w.TicketA]);
        Assert.Equal(CampaignRecipientStates.ReplyReceived, state[w.TicketB]);
    }

    [PostgresFact]
    public async Task Two_claimers_never_take_the_same_row()
    {
        var w = await SeedAsync();
        await EnqueueAsync(w, w.TicketA, w.TicketB);
        var outbox = new CampaignOutboxRepository(_db.DataSource);
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            // The shared test database may hold queued rows of earlier runs.
            await c.ExecuteAsync(
                "UPDATE campaign_messages SET state = 'Cancelled' WHERE state = 'Queued' AND campaign_id <> @id",
                new { id = w.Campaign });
        }

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => outbox.ClaimDueAsync(1, default)));
        var mine = claims.SelectMany(c => c).Where(m => m.CampaignId == w.Campaign).Select(m => m.Id).ToList();

        Assert.Equal(mine.Count, mine.Distinct().Count());
        Assert.Equal(2, mine.Count);
    }

    [PostgresFact]
    public async Task A_round_cannot_hold_the_same_ticket_twice()
    {
        var w = await SeedAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => EnqueueAsync(w, w.TicketA, w.TicketA));
    }

    [PostgresFact]
    public async Task An_interrupted_send_is_settled_without_resending()
    {
        var w = await SeedAsync();
        await EnqueueAsync(w, w.TicketA, w.TicketB);
        Guid sentRow, lostRow;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                "UPDATE campaign_messages SET state = 'Sending', claimed_utc = now() - interval '1 hour' WHERE campaign_id = @id",
                new { id = w.Campaign });
            sentRow = await c.ExecuteScalarAsync<Guid>(
                "SELECT id FROM campaign_messages WHERE campaign_id = @id AND ticket_id = @t", new { id = w.Campaign, t = w.TicketA });
            lostRow = await c.ExecuteScalarAsync<Guid>(
                "SELECT id FROM campaign_messages WHERE campaign_id = @id AND ticket_id = @t", new { id = w.Campaign, t = w.TicketB });
            // The mail for ticket A reached the timeline before the worker stopped.
            await c.ExecuteAsync("""
                INSERT INTO ticket_events (ticket_id, event_type, is_internal, metadata)
                VALUES (@t, 'MailSent', FALSE, jsonb_build_object('campaignMessageId', @m::text))
                """, new { t = w.TicketA, m = sentRow });
        }

        await new CampaignOutboxRepository(_db.DataSource).ReconcileStaleAsync(TimeSpan.FromMinutes(10), default);

        await using var check = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal("Sent", await check.ExecuteScalarAsync<string>("SELECT state FROM campaign_messages WHERE id = @sentRow", new { sentRow }));
        Assert.Equal("Failed", await check.ExecuteScalarAsync<string>("SELECT state FROM campaign_messages WHERE id = @lostRow", new { lostRow }));
    }

    [PostgresFact]
    public async Task A_round_completes_once_when_nothing_is_left_to_send()
    {
        var w = await SeedAsync();
        await EnqueueAsync(w, w.TicketA);
        var outbox = new CampaignOutboxRepository(_db.DataSource);

        Assert.DoesNotContain(await outbox.CompleteSettledRoundsAsync(default), r => r.CampaignId == w.Campaign);
        await MarkAllSentAsync(w.Campaign, DateTime.UtcNow);

        var first = await outbox.CompleteSettledRoundsAsync(default);
        var second = await outbox.CompleteSettledRoundsAsync(default);

        Assert.Single(first, r => r.CampaignId == w.Campaign && r.Sent == 1);
        Assert.DoesNotContain(second, r => r.CampaignId == w.Campaign);
    }
}
