using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.67 (phase 2) — the campaign answer link on real SQL: one link per
/// recipient (a follow-up embeds the same one), no link without a public
/// address or after the proposed date, the customer's answer is one guarded
/// update + a customer-visible timeline event + a trigger pass, it can be
/// changed until the link expires and never afterwards, and a scheduled
/// round is not claimed before its moment.
[Collection(PostgresCollection.Name)]
public sealed class CampaignAnswerDbTests
{
    private readonly PostgresDatabase _db;

    public CampaignAnswerDbTests(PostgresDatabase db) => _db = db;

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static InMemorySettingsService Settings(string baseUrl = "https://desk.example.test")
    {
        var s = new InMemorySettingsService();
        s.Set(SettingKeys.App.PublicBaseUrl, baseUrl);
        s.Set(SettingKeys.Campaigns.ApprovalLinkMaxDays, "60");
        return s;
    }

    private async Task<(Guid Campaign, Guid Ticket, Guid Requester)> SeedAsync(DateTime? proposedUtc)
    {
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        var contact = await seed.ContactAsync();
        var ticket = await seed.TicketAsync("Firewall", contact, queue, status, priority);
        var sender = await seed.UserAsync("Admin");
        var campaign = await new CampaignRepository(_db.DataSource).CreateAsync(
            new NewCampaign("Spring updates", "Update", "<p>{{campaign.approvalLink}}</p>", null, null, proposedUtc, sender), default);
        await new CampaignRecipientRepository(_db.DataSource).AddAsync(
            campaign, new[] { ticket }, CampaignTicketSources.Selection, sender, QueueAccessScope.AdminScope, 500, default);
        return (campaign, ticket, contact);
    }

    private static string TokenOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    private CampaignAnswerService Answers(List<long> triggered, DateTime? now = null)
    {
        var triggers = Fake<ITriggerService>.Create((m, args) =>
        {
            if (m.Name == nameof(ITriggerService.EvaluateAsync)) triggered.Add((long)args![1]!);
            return Task.CompletedTask;
        });
        var clock = now is { } n ? new FixedClock(n) : TimeProvider.System;
        return new CampaignAnswerService(_db.DataSource, TriggerWriteKit.Tickets(_db.DataSource), triggers,
            new NullTicketListNotifier(), new NullCampaignNotifier(), Settings(),
            Fake<IAuditLogger>.Create((_, _) => Task.CompletedTask), clock);
    }

    [PostgresFact]
    public async Task A_recipient_keeps_one_link_and_answers_through_it()
    {
        var now = DateTime.UtcNow;
        var (campaign, ticket, requester) = await SeedAsync(now.AddDays(10));
        var links = new CampaignApprovalLinks(_db.DataSource, Settings(), Protection);
        var proposed = new Dictionary<Guid, DateTime?> { [ticket] = now.AddDays(10) };

        var dry = await links.PlanAsync(campaign, proposed, now, mint: false, default);
        Assert.EndsWith("/campaign-answer/preview", dry[ticket].Url);

        var first = await links.PlanAsync(campaign, proposed, now, mint: true, default);
        var again = await links.PlanAsync(campaign, proposed, now, mint: true, default);
        Assert.StartsWith("https://desk.example.test/campaign-answer/", first[ticket].Url);
        Assert.Equal(first[ticket].Url, again[ticket].Url);

        var token = TokenOf(first[ticket].Url!);
        var triggered = new List<long>();
        var answers = Answers(triggered);

        var view = await answers.GetAsync(token, default);
        Assert.Equal(CampaignAnswerOutcome.Recorded, view.Outcome);
        Assert.Equal("Spring updates", view.View!.CampaignName);
        Assert.Null(view.View.Answer);

        // "Another moment" needs a preferred date inside the picker's bounds (time optional).
        Assert.Equal(CampaignAnswerOutcome.Invalid,
            (await answers.SubmitAsync(token, CampaignDecisions.OtherTime, "x", null, null, null, null, default)).Outcome);
        var tooSoon = DateTime.Parse(view.View.PreferredMinDate).AddDays(-1);
        Assert.Equal(CampaignAnswerOutcome.Invalid,
            (await answers.SubmitAsync(token, CampaignDecisions.OtherTime, "x", tooSoon, null, null, null, default)).Outcome);
        var preferred = DateTime.Parse(view.View.PreferredMinDate).AddDays(6);
        var answered = await answers.SubmitAsync(token, CampaignDecisions.OtherTime, "  Tuesday morning please  ", preferred, 600, null, null, default);
        Assert.Equal(CampaignAnswerOutcome.Recorded, answered.Outcome);
        Assert.Equal("Tuesday morning please", answered.View!.Note);
        Assert.Equal((preferred.ToString("yyyy-MM-dd"), (int?)600), (answered.View.PreferredDate, answered.View.PreferredMinute));
        Assert.NotNull(answered.View.PreferredText);
        var changed = await answers.SubmitAsync(token, CampaignDecisions.Approved, null, preferred, 600, null, null, default);
        Assert.Equal(CampaignDecisions.Approved, changed.View!.Answer);
        Assert.Null(changed.View.PreferredDate); // only kept with "Another moment"
        Assert.Equal(2, triggered.Count);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var row = await c.QuerySingleAsync<(string Decision, string Source, string? Note)>(
            "SELECT decision, decision_source, decision_note FROM campaign_tickets WHERE campaign_id = @campaign AND ticket_id = @ticket",
            new { campaign, ticket });
        Assert.Equal((CampaignDecisions.Approved, CampaignDecisionSources.Customer, (string?)null), row);
        var events = (await c.QueryAsync<(bool IsInternal, Guid? Contact, bool Changed)>("""
            SELECT is_internal, author_contact_id, (metadata ->> 'changed')::boolean
            FROM ticket_events WHERE ticket_id = @ticket AND event_type = 'CampaignAnswer' ORDER BY id
            """, new { ticket })).ToList();
        Assert.Equal(new[] { (false, (Guid?)requester, false), (false, (Guid?)requester, true) }, events);

        // The dashboard counts the link answer as the customer's reply.
        await c.ExecuteAsync("""
            INSERT INTO campaign_rounds (campaign_id, round_no, kind, subject, body_html, sent_by_role)
            VALUES (@campaign, 1, 'Initial', 'S', 'B', 'Admin');
            INSERT INTO campaign_messages (round_id, campaign_id, ticket_id, state, sent_utc)
            SELECT id, @campaign, @ticket, 'Sent', now() - interval '1 day' FROM campaign_rounds WHERE campaign_id = @campaign;
            UPDATE ticket_events SET created_utc = now() WHERE ticket_id = @ticket AND event_type = 'CampaignAnswer';
            """, new { campaign, ticket });
        var board = await new CampaignDashboardReader(_db.DataSource).ReadAsync(campaign, QueueAccessScope.AdminScope, DateTime.UtcNow, default);
        var r = Assert.Single(board.Recipients);
        Assert.Equal(CampaignRecipientStates.ReplyReceived, r.State);
        Assert.Equal("CampaignAnswer", r.Row.ReplyType);
    }

    [PostgresFact]
    public async Task An_expired_or_unknown_link_records_nothing()
    {
        var now = DateTime.UtcNow;
        var (campaign, ticket, _) = await SeedAsync(now.AddDays(2));
        var links = new CampaignApprovalLinks(_db.DataSource, Settings(), Protection);
        var token = TokenOf((await links.PlanAsync(campaign, new Dictionary<Guid, DateTime?> { [ticket] = now.AddDays(2) },
            now, mint: true, default))[ticket].Url!);

        var triggered = new List<long>();
        var later = Answers(triggered, now.AddDays(3));
        Assert.Equal(CampaignAnswerOutcome.Expired, (await later.GetAsync(token, default)).Outcome);
        Assert.Equal(CampaignAnswerOutcome.Expired,
            (await later.SubmitAsync(token, CampaignDecisions.Approved, null, null, null, null, null, default)).Outcome);

        var answers = Answers(triggered);
        Assert.Equal(CampaignAnswerOutcome.NotFound, (await answers.GetAsync("not-a-token", default)).Outcome);
        Assert.Equal(CampaignAnswerOutcome.NotFound,
            (await answers.GetAsync("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", default)).Outcome);
        Assert.Equal(CampaignAnswerOutcome.Invalid,
            (await answers.SubmitAsync(token, "Maybe", null, null, null, null, null, default)).Outcome);
        Assert.Empty(triggered);
    }

    [PostgresFact]
    public async Task No_link_without_a_public_address_or_after_the_proposed_date()
    {
        var now = DateTime.UtcNow;
        var (campaign, ticket, _) = await SeedAsync(null);
        var proposed = new Dictionary<Guid, DateTime?> { [ticket] = now.AddHours(-1) };

        var passed = await new CampaignApprovalLinks(_db.DataSource, Settings(), Protection)
            .PlanAsync(campaign, proposed, now, mint: true, default);
        var noBase = await new CampaignApprovalLinks(_db.DataSource, Settings(baseUrl: ""), Protection)
            .PlanAsync(campaign, new Dictionary<Guid, DateTime?> { [ticket] = null }, now, mint: true, default);

        Assert.Equal(CampaignSkipReasons.ProposedDatePassed, passed[ticket].SkipReason);
        Assert.Equal(CampaignSkipReasons.AnswerLinkUnavailable, noBase[ticket].SkipReason);
    }

    [PostgresFact]
    public async Task A_scheduled_round_waits_for_its_moment()
    {
        var (campaign, ticket, _) = await SeedAsync(null);
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE campaign_messages SET state = 'Cancelled' WHERE state = 'Queued'");
        var outbox = new CampaignOutboxRepository(_db.DataSource);
        var sender = await new DbSeed(_db.DataSource).UserAsync("Admin");

        await outbox.EnqueueRoundAsync(
            new NewCampaignRound(campaign, CampaignRoundKinds.Initial, "S", "B", sender, "Admin", null, null,
                ScheduledUtc: DateTime.UtcNow.AddHours(2)),
            new[] { new NewCampaignMessage(ticket, CampaignMessageStates.Queued, null, "jane@example.test", "S", "B") }, default);

        Assert.Empty(await outbox.ClaimDueAsync(10, default));
        Assert.DoesNotContain(await outbox.CompleteSettledRoundsAsync(default), r => r.CampaignId == campaign);
        var rounds = await new CampaignRepository(_db.DataSource).ListRoundsAsync(campaign, default);
        Assert.NotNull(Assert.Single(rounds).ScheduledUtc);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
