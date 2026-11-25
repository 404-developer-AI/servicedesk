using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Maintenance;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.TicketTemplates;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.67 (phase 3) — the maintenance planner on real SQL: when a plan's mail
/// day comes, one round starts with one campaign and a NEW ticket per member
/// (proposed = the intervention moment); a manual plan is then left without a
/// next date, an automatic one moves on the fixed calendar; running again
/// never duplicates; auto-send goes through the campaign flow as the plan's
/// sender and refuses a sender without access; a member that already had a
/// ticket is switched off instead of deleted; search follows the flag.
[Collection(PostgresCollection.Name)]
public sealed class MaintenancePlannerDbTests
{
    private readonly PostgresDatabase _db;

    public MaintenancePlannerDbTests(PostgresDatabase db) => _db = db;

    private sealed record World(Guid Plan, Guid Queue, Guid ContactA, Guid ContactB, Guid CompanyA, Guid User);

    private async Task<World> SeedAsync(string mode, DateTime? next, bool autoSend = false, string name = "Firewall updates")
    {
        var seed = new DbSeed(_db.DataSource);
        await seed.TaxonomyAsync(); // an active status + priority for the system defaults
        var queue = await seed.QueueAsync("Maintenance");
        var (contactA, contactB) = (await seed.ContactAsync(), await seed.ContactAsync());
        var companyA = await seed.CompanyAsync();
        var user = await seed.UserAsync("Admin");
        var plan = await new MaintenancePlanRepository(_db.DataSource).CreateAsync(new MaintenancePlanInput(
            name, "", queue, null, "{{plan.name}} — {{member.label}} ({{contact.firstName}})", "{{plan.name}} on {{plan.interventionDate}}",
            "<p>Dear {{contact.firstName}}, {{plan.name}}</p>", mode, mode == MaintenanceScheduleModes.Automatic ? 6 : null, next, 540, 14,
            autoSend, null, null, true,
            new[]
            {
                new MaintenanceMemberInput(null, companyA, contactA, "HQ", true),
                new MaintenanceMemberInput(null, null, contactB, "", true),
            }), user, default);
        return new World(plan, queue, contactA, contactB, companyA, user);
    }

    private MaintenanceRoundService Service(DateTime nowUtc, ICampaignService? campaigns = null, IUserService? users = null)
    {
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.App.TimeZone, "Europe/Brussels");
        var taxonomy = new TaxonomyRepository(_db.DataSource);
        var noUsers = users ?? Fake<IUserService>.Create((_, _) => Task.FromResult(false));
        var creator = new SystemTicketCreator(
            TriggerWriteKit.Tickets(_db.DataSource), taxonomy, new QueueStatusScope(taxonomy), noUsers, TriggerWriteKit.Sla(),
            Fake<ITriggerService>.Create((_, _) => Task.CompletedTask), new NullTicketListNotifier(),
            Fake<IAuditLogger>.Create((_, _) => Task.CompletedTask));
        return new MaintenanceRoundService(
            new MaintenanceRoundRepository(_db.DataSource), creator, new TicketTemplateRepository(_db.DataSource),
            new ComposeTokenResolver(_db.DataSource), new CampaignRepository(_db.DataSource),
            new CampaignRecipientRepository(_db.DataSource),
            campaigns ?? Fake<ICampaignService>.Create((m, _) => throw new InvalidOperationException("Unexpected send: " + m.Name)),
            noUsers, new NullCampaignNotifier(), settings, new FixedClock(nowUtc), NullLogger<MaintenanceRoundService>.Instance);
    }

    [PostgresFact]
    public async Task A_manual_plan_mails_every_member_once_from_a_new_ticket()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 3, 31));
        var service = Service(new DateTime(2031, 3, 20, 8, 0, 0, DateTimeKind.Utc)); // 11 days before, inside 14

        await service.RunAsync(default);
        await service.RunAsync(default);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var round = await c.QuerySingleAsync<(string State, Guid? Campaign, string Error)>(
            "SELECT state, campaign_id, last_error FROM maintenance_rounds WHERE plan_id = @Plan", new { w.Plan });
        Assert.True(round.State == MaintenanceRoundStates.Created, round.Error);
        // Plan-level tokens are filled in on the round's campaign; the rest stays for the campaign renderer.
        var mail = await c.QuerySingleAsync<(string Subject, string Body)>(
            "SELECT subject, body_html FROM campaigns WHERE id = @Campaign", new { round.Campaign });
        Assert.StartsWith("Firewall updates on ", mail.Subject);
        Assert.DoesNotContain("{{plan.", mail.Subject);
        Assert.Equal("<p>Dear {{contact.firstName}}, Firewall updates</p>", mail.Body);

        var tickets = (await c.QueryAsync<(string Subject, Guid Requester, Guid? Company, DateTime? Proposed)>("""
            SELECT t.subject, t.requester_contact_id, t.company_id, ct.proposed_utc
            FROM maintenance_round_tickets rt
            JOIN tickets t ON t.id = rt.ticket_id
            JOIN campaign_tickets ct ON ct.ticket_id = t.id AND ct.campaign_id = @Campaign
            ORDER BY t.number
            """, new { round.Campaign })).ToList();
        Assert.Equal(2, tickets.Count);
        Assert.Equal(("Firewall updates — HQ (Jane)", w.ContactA, (Guid?)w.CompanyA), (tickets[0].Subject, tickets[0].Requester, tickets[0].Company));
        Assert.Equal(w.ContactB, tickets[1].Requester);
        // 31 March 2031 09:00 in Brussels (summer time) = 07:00 UTC.
        Assert.All(tickets, t => Assert.Equal(new DateTime(2031, 3, 31, 7, 0, 0), t.Proposed));

        Assert.Null(await c.ExecuteScalarAsync<DateTime?>("SELECT next_intervention_date FROM maintenance_plans WHERE id = @Plan", new { w.Plan }));
        Assert.False(await new MaintenancePlanRepository(_db.DataSource).DeleteUnstartedAsync(w.Plan, default));
        // The date that ran cannot be planned again (one round per plan and date).
        Assert.True(await new MaintenancePlanRepository(_db.DataSource).HasRoundOnAsync(w.Plan, new DateTime(2031, 3, 31), default));
        Assert.False(await new MaintenancePlanRepository(_db.DataSource).HasRoundOnAsync(w.Plan, new DateTime(2031, 4, 1), default));
    }

    [PostgresFact]
    public async Task An_automatic_plan_waits_for_its_mail_day_then_moves_on_the_calendar()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Automatic, new DateTime(2031, 8, 31));
        var service = Service(new DateTime(2031, 7, 1, 8, 0, 0, DateTimeKind.Utc)); // 61 days before

        await service.RunAsync(default);
        await using var c = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM maintenance_rounds WHERE plan_id = @Plan", new { w.Plan }));

        Assert.True(await service.StartNowAsync(w.Plan, default));
        Assert.Equal("Created", await c.ExecuteScalarAsync<string>("SELECT state FROM maintenance_rounds WHERE plan_id = @Plan", new { w.Plan }));
        // 31 Aug + 6 months = 28 Feb (anchor day 31 kept for later rounds).
        Assert.Equal(new DateTime(2032, 2, 29), await c.ExecuteScalarAsync<DateTime>(
            "SELECT next_intervention_date FROM maintenance_plans WHERE id = @Plan", new { w.Plan }));
    }

    [PostgresFact]
    public async Task A_round_starts_at_the_plans_mail_time_on_the_mail_day()
    {
        // Intervention 15 Jan 2032, mail 14 days before (1 Jan) at the default 09:00 Brussels (08:00 UTC).
        var w = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2032, 1, 15));
        await using var c = await _db.DataSource.OpenConnectionAsync();
        const string count = "SELECT COUNT(*) FROM maintenance_rounds WHERE plan_id = @Plan";

        await Service(new DateTime(2032, 1, 1, 7, 59, 0, DateTimeKind.Utc)).RunAsync(default);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(count, new { w.Plan }));
        await Service(new DateTime(2032, 1, 1, 8, 0, 0, DateTimeKind.Utc)).RunAsync(default);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(count, new { w.Plan }));
    }

    [PostgresFact]
    public async Task Auto_send_goes_through_the_campaign_flow_as_the_plans_sender()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 5, 10), autoSend: true);
        var sends = new List<(Guid Campaign, Guid Sender)>();
        var campaigns = Fake<ICampaignService>.Create((m, args) =>
        {
            sends.Add(((Guid)args![0]!, ((TicketMutationActor)args[1]!).UserId));
            return Task.FromResult(new CampaignRoundStarted(Guid.NewGuid(), 1, 2, 0));
        });
        var users = new FakeUserService();
        users.AddWithId(w.User, "planner@example.test", "x", "Admin");
        users.CampaignsEnabled[w.User] = true;

        await Service(new DateTime(2031, 5, 1, 8, 0, 0, DateTimeKind.Utc), campaigns, users).RunAsync(default);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var campaignId = await c.ExecuteScalarAsync<Guid>("SELECT campaign_id FROM maintenance_rounds WHERE plan_id = @Plan", new { w.Plan });
        Assert.Equal(new[] { (campaignId, w.User) }, sends);

        // Without Campaigns access the round stops with a reason; the tickets stay.
        var other = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 5, 10), autoSend: true, name: "Switches");
        await Service(new DateTime(2031, 5, 1, 8, 0, 0, DateTimeKind.Utc), campaigns, new FakeUserService()).RunAsync(default);
        var failed = await c.QuerySingleAsync<(string State, string Error)>(
            "SELECT state, last_error FROM maintenance_rounds WHERE plan_id = @Plan", new { other.Plan });
        Assert.Equal(MaintenanceRoundStates.Failed, failed.State);
        Assert.Contains("can no longer send campaigns", failed.Error);
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM maintenance_round_tickets rt JOIN maintenance_rounds r ON r.id = rt.round_id
            WHERE r.plan_id = @Plan AND rt.ticket_id IS NOT NULL
            """, new { other.Plan }));
    }

    [PostgresFact]
    public async Task A_reminder_goes_once_as_a_follow_up_round_after_its_days()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 5, 20), autoSend: true);
        var requests = new List<(Guid Sender, CampaignRoundRequest Request)>();
        var campaigns = Fake<ICampaignService>.Create((m, args) =>
        {
            requests.Add((((TicketMutationActor)args![1]!).UserId, (CampaignRoundRequest)args[2]!));
            return Task.FromResult(new CampaignRoundStarted(Guid.NewGuid(), requests.Count, 2, 0));
        });
        var users = new FakeUserService();
        users.AddWithId(w.User, "planner@example.test", "x", "Admin");
        users.CampaignsEnabled[w.User] = true;
        await Service(new DateTime(2031, 5, 6, 8, 0, 0, DateTimeKind.Utc), campaigns, users).RunAsync(default);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("""
            UPDATE maintenance_plans SET reminder_enabled = TRUE, reminder_days = 3,
                   reminder_subject = 'Reminder: {{plan.name}}', reminder_body_html = '<p>Still {{plan.interventionDate}}?</p>'
            WHERE id = @Plan;
            INSERT INTO campaign_rounds (campaign_id, round_no, kind, subject, body_html, sent_by_role, started_utc)
            SELECT campaign_id, 1, 'Initial', 's', 'b', 'Admin', '2031-05-06T08:00:00Z' FROM maintenance_rounds WHERE plan_id = @Plan;
            """, new { w.Plan });
        MaintenanceReminderService Reminders(DateTime now) => new(new MaintenanceReminderRepository(_db.DataSource), campaigns, users,
            BrusselsSettings(), new FixedClock(now), NullLogger<MaintenanceReminderService>.Instance);

        Assert.Equal(0, await Reminders(new DateTime(2031, 5, 9, 7, 59, 0, DateTimeKind.Utc)).RunAsync(default));
        Assert.Equal(1, await Reminders(new DateTime(2031, 5, 9, 8, 0, 0, DateTimeKind.Utc)).RunAsync(default));
        Assert.Equal(0, await Reminders(new DateTime(2031, 5, 12, 8, 0, 0, DateTimeKind.Utc)).RunAsync(default)); // once

        var reminder = Assert.Single(requests, r => r.Request.Kind == CampaignRoundKinds.FollowUp);
        Assert.Equal(w.User, reminder.Sender);
        Assert.Equal("Reminder: Firewall updates", reminder.Request.SubjectOverride);
        Assert.DoesNotContain("{{plan.", reminder.Request.BodyHtmlOverride);
        Assert.Equal("Reminder sent to 2 customers.", await c.ExecuteScalarAsync<string>(
            "SELECT reminder_note FROM maintenance_rounds WHERE plan_id = @Plan", new { w.Plan }));
    }

    private static InMemorySettingsService BrusselsSettings()
    {
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.App.TimeZone, "Europe/Brussels");
        return settings;
    }

    [PostgresFact]
    public async Task A_skipped_member_sits_out_one_round_only()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Automatic, new DateTime(2031, 4, 1));
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE maintenance_plan_members SET skip_next_round = TRUE WHERE plan_id = @Plan AND contact_id = @ContactB",
            new { w.Plan, w.ContactB });
        var service = Service(new DateTime(2031, 3, 25, 8, 0, 0, DateTimeKind.Utc));

        await service.RunAsync(default);
        Assert.True(await service.StartNowAsync(w.Plan, default)); // the next round (1 Oct 2031)

        var perRound = (await c.QueryAsync<(DateTime Date, Guid Contact, bool Skipped, bool HasTicket)>("""
            SELECT r.intervention_date, m.contact_id, rt.skipped, rt.ticket_id IS NOT NULL
            FROM maintenance_round_tickets rt
            JOIN maintenance_rounds r ON r.id = rt.round_id
            JOIN maintenance_plan_members m ON m.id = rt.member_id
            WHERE r.plan_id = @Plan
            """, new { w.Plan })).ToList();
        var first = perRound.Where(x => x.Date == new DateTime(2031, 4, 1)).ToList();
        Assert.Contains((new DateTime(2031, 4, 1), w.ContactB, true, false), first);
        Assert.Contains((new DateTime(2031, 4, 1), w.ContactA, false, true), first);
        Assert.Equal(2, perRound.Count(x => x.Date == new DateTime(2031, 10, 1) && x.HasTicket && !x.Skipped));
        Assert.False(await c.ExecuteScalarAsync<bool>(
            "SELECT bool_or(skip_next_round) FROM maintenance_plan_members WHERE plan_id = @Plan", new { w.Plan }));
        // The board's last round is the October one: nobody skipped there.
        Assert.Equal(0, (await new MaintenancePlanRepository(_db.DataSource).GetAsync(w.Plan, default))!.Plan.LastSkipped);
    }

    [PostgresFact]
    public async Task A_member_with_history_is_switched_off_instead_of_deleted()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 4, 1));
        await Service(new DateTime(2031, 3, 25, 8, 0, 0, DateTimeKind.Utc)).RunAsync(default);
        var repo = new MaintenancePlanRepository(_db.DataSource);
        var detail = (await repo.GetAsync(w.Plan, default))!;
        var keep = detail.Members.Single(m => m.ContactId == w.ContactA);

        // Save the plan with only member A: B had a ticket, so it is switched off.
        await repo.UpdateAsync(w.Plan, new MaintenancePlanInput(
            "Firewall updates", "", w.Queue, null, "", "S", "<p>B</p>", MaintenanceScheduleModes.Manual, null, null, 540, 14,
            false, null, null, true, new[] { new MaintenanceMemberInput(keep.Id, w.CompanyA, w.ContactA, "HQ", true) }), w.User, default);

        var after = (await repo.GetAsync(w.Plan, default))!;
        Assert.Equal(2, after.Members.Count);
        Assert.False(after.Members.Single(m => m.ContactId == w.ContactB).IsActive);
        Assert.Equal(1, after.Plan.MemberCount);
    }

    [PostgresFact]
    public async Task Plan_search_follows_the_campaigns_flag()
    {
        var token = $"xgs{Guid.NewGuid():N}"[..12];
        await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 9, 1), name: $"Sophos {token}");
        var source = new MaintenancePlanSearchSource(_db.DataSource);
        var flagged = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(), new HashSet<string> { SearchFeature.Campaigns });

        Assert.Single((await source.SearchAsync(new SearchRequest(token, null, 20, 0), flagged, default)).Hits);
        foreach (var role in new[] { "Agent", "Admin" })
        {
            var plain = new SearchPrincipal(Guid.NewGuid(), role, allowedQueueIds: null);
            Assert.False(source.IsAvailableFor(plain));
            Assert.Empty((await source.SearchAsync(new SearchRequest(token, null, 20, 0), plain, default)).Hits);
        }
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", Array.Empty<Guid>(), new HashSet<string> { SearchFeature.Campaigns });
        Assert.False(source.IsAvailableFor(customer));
    }

    [PostgresFact]
    public async Task Maintenance_notes_live_on_the_member_and_follow_every_round_ticket()
    {
        var w = await SeedAsync(MaintenanceScheduleModes.Manual, new DateTime(2031, 6, 30));
        await Service(new DateTime(2031, 6, 20, 8, 0, 0, DateTimeKind.Utc)).RunAsync(default);
        await using var c = await _db.DataSource.OpenConnectionAsync();
        var (ticketA, numberA) = await c.QuerySingleAsync<(Guid, long)>("""
            SELECT t.id, t.number FROM tickets t JOIN maintenance_round_tickets rt ON rt.ticket_id = t.id
            JOIN maintenance_plan_members m ON m.id = rt.member_id WHERE m.plan_id = @Plan AND m.contact_id = @ContactA
            """, new { w.Plan, w.ContactA });
        var repo = new MaintenanceNoteRepository(_db.DataSource);

        var subject = (await repo.SubjectForTicketAsync(ticketA, default))!;
        Assert.Equal(("Firewall updates", "HQ"), (subject.PlanName, subject.Label));
        Assert.Null(await repo.SubjectForTicketAsync(Guid.NewGuid(), default));
        var otherMember = (await repo.SubjectForMemberAsync(await c.ExecuteScalarAsync<Guid>(
            "SELECT id FROM maintenance_plan_members WHERE plan_id = @Plan AND contact_id = @ContactB", new { w.Plan, w.ContactB }), default))!;

        var token = $"srv{Guid.NewGuid():N}"[..12];
        var original = await repo.AddAsync(subject.MemberId, $"<p>Server {token} in the cabinet</p>", null, w.User, default);
        var addition = await repo.AddAsync(subject.MemberId, "<p>Backup first</p>", ticketA, w.User, default);
        Assert.True(await repo.UpdateAsync(subject.MemberId, addition, "<p>Backup first, then reboot</p>", ticketA, w.User, default));
        Assert.False(await repo.UpdateAsync(otherMember.MemberId, addition, "<p>x</p>", null, w.User, default)); // another member's note
        var extra = await repo.AddAsync(subject.MemberId, "<p>Temporary</p>", null, w.User, default);
        Assert.True(await repo.DeleteAsync(subject.MemberId, extra, ticketA, w.User, default));
        Assert.False(await repo.UpdateAsync(subject.MemberId, extra, "<p>x</p>", null, w.User, default)); // soft-deleted

        var asAdmin = await repo.ListAsync(subject.MemberId, QueueAccessScope.AdminScope, default);
        Assert.Equal(new[] { original, addition }, asAdmin.Select(n => n.Id));
        Assert.Equal((long?)numberA, asAdmin[1].TicketNumber);
        Assert.Equal("<p>Backup first, then reboot</p>", asAdmin[1].BodyHtml);
        // A caller outside the ticket's queue still sees the note, never the ticket number.
        var outsider = await repo.ListAsync(subject.MemberId, new QueueAccessScope(false, new HashSet<Guid>()), default);
        Assert.Null(outsider[1].TicketNumber);
        Assert.Empty(await repo.ListAsync(otherMember.MemberId, QueueAccessScope.AdminScope, default));

        var history = await repo.HistoryAsync(subject.MemberId, QueueAccessScope.AdminScope, default);
        Assert.Equal(new[] { "Added", "Added", "Edited", "Added", "Deleted" }.Order(), history.Select(h => h.Action).Order());

        // Notes are searchable through the plan, for Campaigns flag holders only.
        var source = new MaintenancePlanSearchSource(_db.DataSource);
        var flagged = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(), new HashSet<string> { SearchFeature.Campaigns });
        Assert.Single((await source.SearchAsync(new SearchRequest(token, null, 20, 0), flagged, default)).Hits);
        Assert.Empty((await source.SearchAsync(new SearchRequest(token, null, 20, 0),
            new SearchPrincipal(Guid.NewGuid(), "Agent", allowedQueueIds: null), default)).Hits);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
