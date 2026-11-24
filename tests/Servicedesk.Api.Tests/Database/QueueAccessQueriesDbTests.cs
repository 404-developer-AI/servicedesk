using System.Reflection;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Activity;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Workflow;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// Runs every query that applies the shared queue-access rule
/// (QueueAccessScope) against real PostgreSQL, so a wrong parameter name or
/// SQL mistake fails a test instead of a request. Each test seeds one ticket
/// in queue A and one in queue B, then checks that an agent scoped to A only
/// gets queue-A data (or a masked row), an admin gets both, and an agent
/// with no queues gets nothing.
[Collection(PostgresCollection.Name)]
public sealed class QueueAccessQueriesDbTests
{
    private readonly PostgresDatabase _db;

    public QueueAccessQueriesDbTests(PostgresDatabase db) => _db = db;

    private DbSeed Seed => new(_db.DataSource);

    private static QueueAccessScope AgentScope(params Guid[] queueIds) => new(false, queueIds.ToHashSet());
    private static QueueAccessScope Admin => QueueAccessScope.AdminScope;

    private static string Token(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private sealed record Fixture(
        Guid QueueA, Guid QueueB, Guid TicketA, Guid TicketB, Guid Contact, Guid Status, Guid Priority,
        string Token);

    /// Queue A + B with one ticket each; both subjects start with a unique token.
    private async Task<Fixture> TwoQueuesAsync()
    {
        var (a, s, p) = await Seed.TaxonomyAsync();
        var b = await Seed.QueueAsync("Other queue");
        var contact = await Seed.ContactAsync();
        var token = Token("qx");
        var ta = await Seed.TicketAsync($"{token} printer", contact, a, s, p);
        var tb = await Seed.TicketAsync($"{token} laptop", contact, b, s, p);
        return new Fixture(a, b, ta, tb, contact, s, p, token);
    }

    private static InsightsRange Window()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new InsightsRange(today.AddDays(-1), today.AddDays(1));
    }

    private static InsightsClock UtcClock() =>
        new(TimeZoneInfo.Utc, DateOnly.FromDateTime(DateTime.UtcNow));

    // ---- 1. activity feed ---------------------------------------------------

    private ActivityFeedQuery Feed => new(_db.DataSource);

    [PostgresFact]
    public async Task ActivityFeed_masks_tickets_in_queues_the_viewer_cannot_access()
    {
        var f = await TwoQueuesAsync();
        var author = await Seed.UserAsync();
        await Seed.EventAsync(f.TicketA, "Note", isInternal: true, authorUserId: author);
        await Seed.EventAsync(f.TicketB, "Note", isInternal: true, authorUserId: author);

        void AssertMasked(IReadOnlyList<ActivityFeedEntry> rows, bool adminView)
        {
            var mine = rows.Where(r => r.AgentId == author).ToList();
            Assert.Equal(2, mine.Count);
            var inA = Assert.Single(mine, r => r.EntityId == f.TicketA.ToString());
            Assert.Equal($"{f.Token} printer", inA.TicketSubject);
            Assert.False(inA.TicketRestricted);

            if (adminView)
            {
                var inB = Assert.Single(mine, r => r.EntityId == f.TicketB.ToString());
                Assert.Equal($"{f.Token} laptop", inB.TicketSubject);
                Assert.False(inB.TicketRestricted);
            }
            else
            {
                var restricted = Assert.Single(mine, r => r.TicketRestricted);
                Assert.Null(restricted.TicketSubject);
                Assert.Null(restricted.TicketNumber);
                Assert.Null(restricted.EntityId);
            }
        }

        AssertMasked(await Feed.ListRecentAsync(AgentScope(f.QueueA), 100), adminView: false);
        AssertMasked(await Feed.ListRecentAsync(Admin, 100), adminView: true);

        var filter = new ActivityFeedFilter(AgentId: author);
        AssertMasked((await Feed.ListAsync(AgentScope(f.QueueA), filter)).Items, adminView: false);
        AssertMasked((await Feed.ListAsync(Admin, filter)).Items, adminView: true);

        // Agent without queues: every ticket row is masked.
        var none = (await Feed.ListAsync(AgentScope(), filter)).Items;
        Assert.Equal(2, none.Count);
        Assert.All(none, r => Assert.True(r.TicketRestricted));
    }

    [PostgresFact]
    public async Task ActivityFeed_subject_search_never_matches_a_hidden_ticket()
    {
        var f = await TwoQueuesAsync();
        var author = await Seed.UserAsync();
        await Seed.EventAsync(f.TicketA, "Note", isInternal: true, authorUserId: author);
        await Seed.EventAsync(f.TicketB, "Note", isInternal: true, authorUserId: author);

        var search = new ActivityFeedFilter(AgentId: author, Search: f.Token);
        var agentHits = (await Feed.ListAsync(AgentScope(f.QueueA), search)).Items;
        Assert.Equal(new[] { f.TicketA.ToString() }, agentHits.Select(r => r.EntityId).ToArray());

        var adminHits = (await Feed.ListAsync(Admin, search)).Items;
        Assert.Equal(2, adminHits.Count);
    }

    // ---- 2. insights ----------------------------------------------------------

    private InsightsService Insights => new(_db.DataSource, ThrowingSettings.Create());

    [PostgresFact]
    public async Task Insights_ticket_counts_cover_only_accessible_queues()
    {
        var f = await TwoQueuesAsync();
        var range = Window();

        var agent = await Insights.GetTicketCountsAsync(
            InsightsTicketFilter.NewTickets, range, InsightsGranularity.Day, AgentScope(f.QueueA));
        var agentQueue = Assert.Single(agent.Queues);
        Assert.Equal(f.QueueA, agentQueue.Id);
        Assert.Equal(1, agent.Buckets.Sum(b => b.Counts.Sum()));

        var admin = await Insights.GetTicketCountsAsync(
            InsightsTicketFilter.NewTickets, range, InsightsGranularity.Day, Admin);
        var ids = admin.Queues.Select(q => q.Id).ToList();
        Assert.Contains(f.QueueA, ids);
        Assert.Contains(f.QueueB, ids);
        foreach (var queueId in new[] { f.QueueA, f.QueueB })
        {
            var idx = ids.IndexOf(queueId);
            Assert.Equal(1, admin.Buckets.Sum(b => b.Counts[idx]));
        }

        var none = await Insights.GetTicketCountsAsync(
            InsightsTicketFilter.NewTickets, range, InsightsGranularity.Day, AgentScope());
        Assert.Empty(none.Queues);
    }

    [PostgresFact]
    public async Task Insights_closed_counts_execute_for_agent_and_admin()
    {
        var f = await TwoQueuesAsync();
        var closed = new InsightsTicketFilter(InsightsMetric.Closed, ClosedOutcomes.All);
        var range = Window();

        var agent = await Insights.GetTicketCountsAsync(closed, range, InsightsGranularity.Day, AgentScope(f.QueueA));
        Assert.Equal(f.QueueA, Assert.Single(agent.Queues).Id);
        await Insights.GetTicketCountsAsync(closed, range, InsightsGranularity.Day, Admin);
    }

    [PostgresFact]
    public async Task Insights_ticket_list_respects_access_and_queue_selection()
    {
        var f = await TwoQueuesAsync();
        var range = Window();
        var both = new[] { f.QueueA, f.QueueB };

        var agent = await Insights.ListTicketsAsync(
            InsightsTicketFilter.NewTickets, range, AgentScope(f.QueueA), null, 0, 100);
        Assert.Equal(1, agent.Total);
        Assert.Equal(f.TicketA, Assert.Single(agent.Items).Id);

        // A selection can narrow the access scope but never widen it.
        var widened = await Insights.ListTicketsAsync(
            InsightsTicketFilter.NewTickets, range, AgentScope(f.QueueA), both, 0, 100);
        Assert.Equal(f.TicketA, Assert.Single(widened.Items).Id);

        var adminSelected = await Insights.ListTicketsAsync(
            InsightsTicketFilter.NewTickets, range, Admin, both, 0, 100);
        Assert.Equal(2, adminSelected.Total);
        Assert.Equal(
            new[] { f.TicketA, f.TicketB }.Order().ToArray(),
            adminSelected.Items.Select(i => i.Id).Order().ToArray());

        var adminNarrowed = await Insights.ListTicketsAsync(
            InsightsTicketFilter.NewTickets, range, Admin, new[] { f.QueueB }, 0, 100);
        Assert.Equal(f.TicketB, Assert.Single(adminNarrowed.Items).Id);

        var none = await Insights.ListTicketsAsync(
            InsightsTicketFilter.NewTickets, range, AgentScope(), null, 0, 100);
        Assert.Equal(0, none.Total);
    }

    // ---- 3. agent insights ---------------------------------------------------

    private AgentInsightsService AgentInsights => new(_db.DataSource);

    [PostgresFact]
    public async Task AgentInsights_activity_tickets_and_opened_without_action_are_access_scoped()
    {
        var f = await TwoQueuesAsync();
        var userId = await Seed.UserAsync();
        await Seed.EventAsync(f.TicketA, "Note", isInternal: true, authorUserId: userId);
        await Seed.EventAsync(f.TicketB, "Note", isInternal: true, authorUserId: userId);
        await Seed.OpenSessionAsync(userId, f.TicketA);
        await Seed.OpenSessionAsync(userId, f.TicketB);

        var range = Window();
        var clock = UtcClock();
        var agents = new[] { new InsightsAgent(userId, "Agent Example", "agent@example.test", "Agent", 0) };

        var activityA = await AgentInsights.GetActivityAsync(
            agents, range, InsightsGranularity.Day, clock, AgentScope(f.QueueA), 0);
        Assert.Equal(1, activityA.Totals[0].Tickets);
        Assert.Equal(1, activityA.Totals[0].OpenedNoAction);

        var activityAdmin = await AgentInsights.GetActivityAsync(
            agents, range, InsightsGranularity.Day, clock, Admin, 0);
        Assert.Equal(2, activityAdmin.Totals[0].Tickets);
        Assert.Equal(2, activityAdmin.Totals[0].OpenedNoAction);

        var activityNone = await AgentInsights.GetActivityAsync(
            agents, range, InsightsGranularity.Day, clock, AgentScope(), 0);
        Assert.Equal(0, activityNone.Totals[0].Tickets);

        var ticketsA = await AgentInsights.ListTicketsAsync(
            userId, range, clock, AgentScope(f.QueueA), AgentTicketSort.Recent, 0, 50);
        Assert.Equal(1, ticketsA.Total);
        Assert.Equal(f.TicketA, Assert.Single(ticketsA.Items).Id);

        var ticketsAdmin = await AgentInsights.ListTicketsAsync(
            userId, range, clock, Admin, AgentTicketSort.PeriodTime, 0, 50);
        Assert.Equal(2, ticketsAdmin.Total);

        var openedA = await AgentInsights.ListOpenedWithoutActionAsync(
            userId, range, clock, AgentScope(f.QueueA), 0, 0, 50);
        Assert.Equal(1, openedA.Total);
        Assert.Equal(f.TicketA, Assert.Single(openedA.Items).TicketId);

        var openedAdmin = await AgentInsights.ListOpenedWithoutActionAsync(
            userId, range, clock, Admin, 0, 0, 50);
        Assert.Equal(2, openedAdmin.Total);
    }

    // ---- 4. workflow report ---------------------------------------------------

    [PostgresFact]
    public async Task Workflow_report_is_cut_to_the_callers_queues()
    {
        var f = await TwoQueuesAsync();
        var userId = await Seed.UserAsync();
        await Seed.EventAsync(f.TicketA, "MailSent", isInternal: false, authorUserId: userId);
        await Seed.EventAsync(f.TicketB, "MailSent", isInternal: false, authorUserId: userId);
        var service = new WorkflowReportService(_db.DataSource, ThrowingSettings.Create());
        var range = Window();

        var agentReport = await service.GetReportAsync(range, null, AgentScope(f.QueueA));
        var agentRow = Assert.Single(agentReport.Agents, a => a.AgentId == userId);
        Assert.Equal(1, agentRow.CallBeforeMail.Total);

        var adminReport = await service.GetReportAsync(range, null, Admin);
        Assert.Equal(2, Assert.Single(adminReport.Agents, a => a.AgentId == userId).CallBeforeMail.Total);

        var agentCases = await service.GetCasesAsync(
            range, null, AgentScope(f.QueueA), userId, WorkflowKpi.CallBeforeMail);
        Assert.Equal(f.TicketA, Assert.Single(agentCases).TicketId);

        var noneReport = await service.GetReportAsync(range, null, AgentScope());
        Assert.DoesNotContain(noneReport.Agents, a => a.AgentId == userId);
    }

    [PostgresFact]
    public async Task Workflow_report_with_a_view_executes_for_agent_and_admin()
    {
        var f = await TwoQueuesAsync();
        var service = new WorkflowReportService(_db.DataSource, ThrowingSettings.Create());
        var range = Window();
        var viewId = Guid.NewGuid(); // no such view: the view-bound queries run and match nothing

        await service.GetReportAsync(range, viewId, AgentScope(f.QueueA));
        await service.GetReportAsync(range, viewId, Admin);
    }

    // ---- 5. ticket list + picker ---------------------------------------------

    private TicketRepository Tickets => TriggerWriteKit.Tickets(_db.DataSource);

    [PostgresFact]
    public async Task Ticket_list_search_applies_the_queue_access_list()
    {
        var f = await TwoQueuesAsync();

        var agent = await Tickets.SearchAsync(
            new TicketQuery(RequesterContactId: f.Contact, AccessibleQueueIds: new[] { f.QueueA }),
            VisibilityScope.All, null, null, default);
        Assert.Equal(f.TicketA, Assert.Single(agent.Items).Id);

        var admin = await Tickets.SearchAsync(
            new TicketQuery(RequesterContactId: f.Contact, AccessibleQueueIds: null),
            VisibilityScope.All, null, null, default);
        Assert.Equal(
            new[] { f.TicketA, f.TicketB }.Order().ToArray(),
            admin.Items.Select(i => i.Id).Order().ToArray());

        var none = await Tickets.SearchAsync(
            new TicketQuery(RequesterContactId: f.Contact, AccessibleQueueIds: Array.Empty<Guid>()),
            VisibilityScope.All, null, null, default);
        Assert.Empty(none.Items);

        // Combined with a view's own queue multi-select: still AND.
        var narrowed = await Tickets.SearchAsync(
            new TicketQuery(RequesterContactId: f.Contact, AccessibleQueueIds: new[] { f.QueueA },
                QueueIds: new[] { f.QueueB }),
            VisibilityScope.All, null, null, default);
        Assert.Empty(narrowed.Items);
    }

    [PostgresFact]
    public async Task Ticket_picker_search_applies_the_queue_access_list()
    {
        var f = await TwoQueuesAsync();
        var otherTicket = await Seed.TicketAsync("Unrelated", f.Contact, f.QueueA, f.Status, f.Priority);

        var agent = await Tickets.SearchPickerAsync(
            f.Token, otherTicket, new[] { f.QueueA }, recentForUserId: null, limit: 50, default);
        Assert.Equal(f.TicketA, Assert.Single(agent).Id);

        var admin = await Tickets.SearchPickerAsync(
            f.Token, otherTicket, null, recentForUserId: null, limit: 50, default);
        Assert.Equal(
            new[] { f.TicketA, f.TicketB }.Order().ToArray(),
            admin.Select(h => h.Id).Order().ToArray());

        var none = await Tickets.SearchPickerAsync(
            f.Token, otherTicket, Array.Empty<Guid>(), recentForUserId: null, limit: 50, default);
        Assert.Empty(none);
    }

    [PostgresFact]
    public async Task Ticket_picker_recent_and_fill_paths_apply_the_queue_access_list()
    {
        var f = await TwoQueuesAsync();
        var user = await Seed.UserAsync();
        var exclude = await Seed.TicketAsync("Source", f.Contact, f.QueueA, f.Status, f.Priority);
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(c,
                "INSERT INTO user_recent_tickets (user_id, ticket_id) VALUES (@user, @a), (@user, @b)",
                new { user, a = f.TicketA, b = f.TicketB });
        }

        // A limit of 2 is filled by the recent arm alone; the larger limit also runs the backfill arm.
        foreach (var limit in new[] { 2, 50 })
        {
            var agent = await Tickets.SearchPickerAsync(null, exclude, new[] { f.QueueA }, user, limit, default);
            Assert.Contains(agent, h => h.Id == f.TicketA);
            Assert.DoesNotContain(agent, h => h.Id == f.TicketB);

            var admin = await Tickets.SearchPickerAsync(null, exclude, null, user, limit, default);
            Assert.Contains(admin, h => h.Id == f.TicketA);
            Assert.Contains(admin, h => h.Id == f.TicketB);
        }
    }

    // ---- 6. project overview --------------------------------------------------

    [PostgresFact]
    public async Task Project_overview_lists_only_accessible_linked_tickets_and_counts_the_rest_as_hidden()
    {
        var f = await TwoQueuesAsync();
        var project = await Seed.TicketAsync($"{f.Token} project", f.Contact, f.QueueA, f.Status, f.Priority, isProject: true);
        await Seed.LinkToProjectAsync(f.TicketA, project);
        await Seed.LinkToProjectAsync(f.TicketB, project);
        var repo = new TicketProjectRepository(_db.DataSource);

        var agent = await repo.GetOverviewAsync(project, new[] { f.QueueA }, default);
        Assert.NotNull(agent);
        Assert.Equal(f.TicketA, Assert.Single(agent!.Tickets).Id);
        Assert.Equal(1, agent.HiddenTicketCount);

        var admin = await repo.GetOverviewAsync(project, null, default);
        Assert.NotNull(admin);
        Assert.Equal(2, admin!.Tickets.Count);
        Assert.Equal(0, admin.HiddenTicketCount);

        var none = await repo.GetOverviewAsync(project, Array.Empty<Guid>(), default);
        Assert.NotNull(none);
        Assert.Empty(none!.Tickets);
        Assert.Equal(2, none.HiddenTicketCount);
    }

    // ---- 7. search sources ----------------------------------------------------

    private static SearchRequest Query(string q) => new(q, Type: null, Limit: 50, Offset: 0);

    private static SearchPrincipal AgentPrincipal(params Guid[] queues) => new(Guid.NewGuid(), "Agent", queues);
    private static SearchPrincipal AdminPrincipal() => new(Guid.NewGuid(), "Admin", allowedQueueIds: null);

    private static async Task AssertAgentAdminAndNoQueuesAsync(
        ISearchSource source, string token, Guid queueA, Guid ticketA, Guid ticketB)
    {
        var agent = await source.SearchAsync(Query(token), AgentPrincipal(queueA), default);
        Assert.Equal(1, agent.Hits.Count);
        Assert.Equal(ticketA.ToString(), TicketIdOf(agent.Hits[0]));

        var admin = await source.SearchAsync(Query(token), AdminPrincipal(), default);
        Assert.Equal(
            new[] { ticketA.ToString(), ticketB.ToString() }.Order().ToArray(),
            admin.Hits.Select(TicketIdOf).Order().ToArray());

        var none = await source.SearchAsync(Query(token), AgentPrincipal(), default);
        Assert.Empty(none.Hits);
    }

    private static string? TicketIdOf(SearchHit hit) => hit.Meta!["ticketId"];

    [PostgresFact]
    public async Task Ticket_form_search_is_queue_scoped()
    {
        var f = await TwoQueuesAsync();
        await Seed.FormAsync(f.TicketA, $"Form {f.Token}");
        await Seed.FormAsync(f.TicketB, $"Form {f.Token}");

        await AssertAgentAdminAndNoQueuesAsync(
            new TicketFormSearchSource(_db.DataSource), f.Token, f.QueueA, f.TicketA, f.TicketB);
    }

    [PostgresFact]
    public async Task Checklist_item_search_is_queue_scoped()
    {
        var f = await TwoQueuesAsync();
        await Seed.ChecklistItemAsync(f.TicketA, $"Step {f.Token}");
        await Seed.ChecklistItemAsync(f.TicketB, $"Step {f.Token}");

        await AssertAgentAdminAndNoQueuesAsync(
            new TicketChecklistItemSearchSource(_db.DataSource), f.Token, f.QueueA, f.TicketA, f.TicketB);
    }

    [PostgresFact]
    public async Task Intake_submission_search_is_queue_scoped()
    {
        var f = await TwoQueuesAsync();
        await Seed.IntakeSubmissionAsync(f.TicketA, $"Answer {f.Token}");
        await Seed.IntakeSubmissionAsync(f.TicketB, $"Answer {f.Token}");

        await AssertAgentAdminAndNoQueuesAsync(
            new IntakeSubmissionSearchSource(_db.DataSource), f.Token, f.QueueA, f.TicketA, f.TicketB);
    }

    // Settings reads fail → services that tolerate it fall back to their defaults.
    private class ThrowingSettings : DispatchProxy
    {
        public static ISettingsService Create() => Create<ISettingsService, ThrowingSettings>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("settings unavailable in this test");
    }
}
