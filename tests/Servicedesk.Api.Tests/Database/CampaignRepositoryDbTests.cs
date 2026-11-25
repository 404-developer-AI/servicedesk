using Dapper;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Campaigns;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.67 — the remaining Campaigns SQL on a real database: list/detail/
/// round counts, the send-time recheck context, decisions and proposed dates
/// (queue-scoped), removing only never-mailed recipients, and cancel/retry
/// limited to the caller's queues.
[Collection(PostgresCollection.Name)]
public sealed class CampaignRepositoryDbTests
{
    private readonly PostgresDatabase _db;

    public CampaignRepositoryDbTests(PostgresDatabase db) => _db = db;

    private static QueueAccessScope Only(Guid queue) => new(false, new HashSet<Guid> { queue });

    [PostgresFact]
    public async Task Campaign_rows_rounds_and_recipient_writes_work_end_to_end()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queueA, status, priority) = await seed.TaxonomyAsync();
        var queueB = await seed.QueueAsync("Other");
        var project = await seed.TicketAsync("Firewall project", await seed.ContactAsync(), queueA, status, priority, isProject: true);
        var a = await seed.TicketAsync("Firewall A", await seed.ContactAsync(), queueA, status, priority);
        var b = await seed.TicketAsync("Firewall B", await seed.ContactAsync(), queueB, status, priority);
        await seed.LinkToProjectAsync(a, project);
        await seed.LinkToProjectAsync(b, project);
        var sender = await seed.UserAsync("Agent");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE users SET campaigns_enabled = TRUE WHERE id = @sender", new { sender });

        var campaigns = new CampaignRepository(_db.DataSource);
        var recipients = new CampaignRecipientRepository(_db.DataSource);
        var outbox = new CampaignOutboxRepository(_db.DataSource);
        var id = await campaigns.CreateAsync(new NewCampaign("Spring updates", "Update", "<p>Hi</p>", null, project, null, sender), default);

        // The project's open linked tickets, filtered to the caller's queues.
        var linked = await recipients.OpenProjectTicketIdsAsync(project, default);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), linked.OrderBy(x => x));
        var added = await recipients.AddAsync(id, linked, CampaignTicketSources.Project, sender, QueueAccessScope.AdminScope, 500, default);
        Assert.Equal(2, added.Added);

        // Proposed date + decision only touch rows in the caller's queues.
        var when = DateTime.UtcNow.AddDays(14);
        Assert.Equal(1, await recipients.SetProposedAsync(id, new[] { a, b }, when, Only(queueA), default));
        Assert.False(await recipients.SetDecisionAsync(id, b, CampaignDecisions.Approved, sender, Only(queueA), default));
        Assert.True(await recipients.SetDecisionAsync(id, a, CampaignDecisions.OtherTime, sender, Only(queueA), default));

        var (roundId, roundNo) = await outbox.EnqueueRoundAsync(
            new NewCampaignRound(id, CampaignRoundKinds.Initial, "Update", "<p>Hi</p>", sender, "Agent", null, null),
            new[]
            {
                new NewCampaignMessage(a, CampaignMessageStates.Queued, null, "jane@example.test", "Update", "<p>Hi</p>"),
                new NewCampaignMessage(b, CampaignMessageStates.Queued, null, "jane@example.test", "Update", "<p>Hi</p>"),
            }, default);
        Assert.Equal(1, roundNo);

        // A mailed recipient can no longer be removed.
        Assert.Equal(0, await recipients.RemoveUnsentAsync(id, new[] { a }, QueueAccessScope.AdminScope, default));

        // Cancel is limited to the caller's queues; retry likewise.
        Assert.Equal(1, await outbox.CancelQueuedAsync(id, Only(queueA), default));
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE campaign_messages SET state = 'Failed' WHERE round_id = @roundId", new { roundId });
        Assert.Equal(1, await outbox.RetryFailedAsync(id, Only(queueB), default));

        var list = await campaigns.ListAsync(includeArchived: false, projectTicketId: project, default);
        var item = Assert.Single(list);
        Assert.Equal(2, item.TicketCount);
        Assert.Equal(1, item.QueuedCount);
        Assert.Equal(1, item.FailedCount);

        var detail = await campaigns.GetAsync(id, default);
        Assert.NotNull(detail);
        Assert.Equal(CampaignStates.Active, detail!.State);
        Assert.Equal(project, detail.ProjectTicketId);

        var rounds = await campaigns.ListRoundsAsync(id, default);
        var round = Assert.Single(rounds);
        Assert.Equal((2, 1, 1), (round.Total, round.Queued, round.Failed));

        var context = await outbox.GetSendContextAsync(a, sender, default);
        Assert.True(context.TicketExists);
        Assert.True(context.SenderFlag && context.SenderActive);
        Assert.Equal("Agent", context.SenderRole);
        Assert.Equal(queueA, context.QueueId);
        Assert.False(context.IsProject);

        var missing = await outbox.GetSendContextAsync(Guid.NewGuid(), null, default);
        Assert.False(missing.TicketExists);
        Assert.False(missing.SenderFlag);

        Assert.False(await campaigns.DeleteUnsentAsync(id, default));
        var draft = await campaigns.CreateAsync(new NewCampaign("Never sent", "", "", null, null, null, sender), default);
        Assert.True(await campaigns.UpdateAsync(draft, new CampaignUpdate(Name: "Renamed", State: CampaignStates.Archived), default));
        Assert.Equal("Renamed", (await campaigns.GetAsync(draft, default))!.Name);
        Assert.True(await campaigns.DeleteUnsentAsync(draft, default));
    }

    [PostgresFact]
    public async Task Compose_tokens_resolve_for_many_tickets_in_one_call()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        var a = await seed.TicketAsync("Firewall A", await seed.ContactAsync(), queue, status, priority);
        var b = await seed.TicketAsync("Firewall B", await seed.ContactAsync(), queue, status, priority);
        var resolver = new Servicedesk.Infrastructure.ComposeTemplates.ComposeTokenResolver(_db.DataSource);

        var maps = await resolver.ResolveForTicketsAsync(new[] { a, b, Guid.NewGuid() }, "agent@example.test", default);
        var single = await resolver.ResolveForTicketAsync(a, "agent@example.test", default);

        Assert.Equal(2, maps.Count);
        Assert.Equal("Jane", maps[a]["{{contact.firstName}}"]);
        Assert.Equal("Firewall B", maps[b]["{{ticket.subject}}"]);
        Assert.Equal(maps[a]["{{ticket.number}}"], single["{{ticket.number}}"]);
        Assert.Equal("agent@example.test", single["{{agent.email}}"]);
    }
}
