using Dapper;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Tickets.Following;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — following tickets against a real database: "has news" is
/// derived at read time from any change (event, field update, note reply,
/// like), seen clears it, the list obeys queue access and the cap, and a
/// merge carries followers over to the surviving ticket.
[Collection(PostgresCollection.Name)]
public sealed class TicketFollowDbTests
{
    private readonly PostgresDatabase _db;

    public TicketFollowDbTests(PostgresDatabase db) => _db = db;

    private TicketFollowRepository Repo => new(_db.DataSource);

    private async Task<(DbSeed Seed, Guid Queue, Guid Status, Guid Priority, Guid Contact, Guid User)> WorldAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        return (seed, q, s, p, await seed.ContactAsync(), await seed.UserAsync("Agent"));
    }

    private async Task<FollowedTicket> RowAsync(Guid userId, Guid ticketId, Guid queueId)
        => Assert.Single(await Repo.ListAsync(userId, new QueueAccessScope(false, new HashSet<Guid> { queueId }), default),
            r => r.TicketId == ticketId);

    /// Pushes last_seen into the past so a change "now" counts as news
    /// without sleeping in the test.
    private async Task BackdateSeenAsync(Guid userId, Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE ticket_follows SET last_seen_utc = now() - interval '1 hour' WHERE user_id = @userId AND ticket_id = @ticketId",
            new { userId, ticketId });
    }

    private async Task BackdateTicketAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("""
            UPDATE tickets SET updated_utc = now() - interval '2 hours' WHERE id = @ticketId;
            UPDATE ticket_events SET created_utc = now() - interval '2 hours' WHERE ticket_id = @ticketId;
            """, new { ticketId });
    }

    [PostgresFact]
    public async Task Following_twice_keeps_one_row_and_a_fresh_follow_has_no_news()
    {
        var w = await WorldAsync();
        var t = await w.Seed.TicketAsync("Printer offline", w.Contact, w.Queue, w.Status, w.Priority);

        Assert.Equal(FollowResult.Followed, await Repo.FollowAsync(w.User, t, 200, default));
        Assert.Equal(FollowResult.AlreadyFollowing, await Repo.FollowAsync(w.User, t, 200, default));

        Assert.False((await RowAsync(w.User, t, w.Queue)).HasActivity);
    }

    [PostgresFact]
    public async Task The_cap_refuses_one_more_ticket()
    {
        var w = await WorldAsync();
        var a = await w.Seed.TicketAsync("First", w.Contact, w.Queue, w.Status, w.Priority);
        var b = await w.Seed.TicketAsync("Second", w.Contact, w.Queue, w.Status, w.Priority);

        Assert.Equal(FollowResult.Followed, await Repo.FollowAsync(w.User, a, 1, default));
        Assert.Equal(FollowResult.LimitReached, await Repo.FollowAsync(w.User, b, 1, default));
        Assert.Equal(FollowResult.AlreadyFollowing, await Repo.FollowAsync(w.User, a, 1, default));
    }

    [PostgresFact]
    public async Task Any_change_after_seen_is_news_and_seen_clears_it()
    {
        var w = await WorldAsync();
        var author = await w.Seed.UserAsync("Agent");
        var t = await w.Seed.TicketAsync("Mailbox full", w.Contact, w.Queue, w.Status, w.Priority);
        var note = await w.Seed.EventAsync(t, "Note", isInternal: true, "Check the quota", author);
        await BackdateTicketAsync(t);
        await Repo.FollowAsync(w.User, t, 200, default);
        await BackdateSeenAsync(w.User, t);
        Assert.False((await RowAsync(w.User, t, w.Queue)).HasActivity); // everything older than last seen

        // A new event (post, mail, status-change row …).
        await w.Seed.EventAsync(t, "Comment", isInternal: false);
        Assert.True((await RowAsync(w.User, t, w.Queue)).HasActivity);
        Assert.True(await Repo.MarkSeenAsync(w.User, t, default));
        Assert.False((await RowAsync(w.User, t, w.Queue)).HasActivity);

        await using var c = await _db.DataSource.OpenConnectionAsync();

        // A field update that only bumps updated_utc.
        await BackdateTicketAsync(t);
        await BackdateSeenAsync(w.User, t);
        await c.ExecuteAsync("UPDATE tickets SET updated_utc = now() WHERE id = @t", new { t });
        Assert.True((await RowAsync(w.User, t, w.Queue)).HasActivity);

        // A note reply.
        await BackdateTicketAsync(t);
        await BackdateSeenAsync(w.User, t);
        await c.ExecuteAsync(
            "INSERT INTO ticket_note_replies (root_event_id, author_user_id, body_html, body_text) VALUES (@note, @author, '<p>Done</p>', 'Done')",
            new { note, author });
        Assert.True((await RowAsync(w.User, t, w.Queue)).HasActivity);

        // A like.
        await c.ExecuteAsync("UPDATE ticket_note_replies SET created_utc = now() - interval '2 hours' WHERE root_event_id = @note", new { note });
        await BackdateSeenAsync(w.User, t);
        Assert.False((await RowAsync(w.User, t, w.Queue)).HasActivity);
        await c.ExecuteAsync("INSERT INTO ticket_note_likes (event_id, user_id) VALUES (@note, @author)", new { note, author });
        Assert.True((await RowAsync(w.User, t, w.Queue)).HasActivity);
    }

    [PostgresFact]
    public async Task Tickets_in_a_queue_without_access_drop_out_of_the_list()
    {
        var w = await WorldAsync();
        var otherQueue = await w.Seed.QueueAsync("Other");
        var visible = await w.Seed.TicketAsync("Visible", w.Contact, w.Queue, w.Status, w.Priority);
        var hidden = await w.Seed.TicketAsync("Hidden", w.Contact, otherQueue, w.Status, w.Priority);
        await Repo.FollowAsync(w.User, visible, 200, default);
        await Repo.FollowAsync(w.User, hidden, 200, default);

        var list = await Repo.ListAsync(w.User, new QueueAccessScope(false, new HashSet<Guid> { w.Queue }), default);
        Assert.Equal(new[] { visible }, list.Select(r => r.TicketId));

        var none = await Repo.ListAsync(w.User, new QueueAccessScope(false, new HashSet<Guid>()), default);
        Assert.Empty(none);
        Assert.Equal(2, (await Repo.ListAsync(w.User, QueueAccessScope.AdminScope, default)).Count);
    }

    [PostgresFact]
    public async Task Seen_and_unfollow_without_a_follow_are_no_ops()
    {
        var w = await WorldAsync();
        var t = await w.Seed.TicketAsync("Not followed", w.Contact, w.Queue, w.Status, w.Priority);
        Assert.False(await Repo.MarkSeenAsync(w.User, t, default));
        Assert.False(await Repo.UnfollowAsync(w.User, t, default));
    }

    [PostgresFact]
    public async Task A_merge_moves_followers_to_the_target_without_duplicates()
    {
        var w = await WorldAsync();
        var other = await w.Seed.UserAsync("Agent");
        var source = await w.Seed.TicketAsync("Duplicate", w.Contact, w.Queue, w.Status, w.Priority);
        var target = await w.Seed.TicketAsync("Original", w.Contact, w.Queue, w.Status, w.Priority);
        await Repo.FollowAsync(w.User, source, 200, default);
        await Repo.FollowAsync(w.User, target, 200, default);  // follows both
        await Repo.FollowAsync(other, source, 200, default);   // follows the source only

        var result = await new TicketRepository(_db.DataSource).MergeAsync(source, target, w.User, true, default);
        Assert.NotNull(result);
        Assert.True(result!.Success);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var rows = (await c.QueryAsync<(Guid, Guid)>(
            "SELECT user_id, ticket_id FROM ticket_follows WHERE ticket_id IN (@source, @target) ORDER BY user_id",
            new { source, target })).ToList();
        Assert.Equal(new[] { (w.User, target), (other, target) }.OrderBy(r => r.Item1), rows);
    }

    [PostgresFact]
    public async Task Deleting_the_ticket_removes_its_follows()
    {
        var w = await WorldAsync();
        var t = await w.Seed.TicketAsync("Short-lived", w.Contact, w.Queue, w.Status, w.Priority);
        await Repo.FollowAsync(w.User, t, 200, default);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM tickets WHERE id = @t", new { t });
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM ticket_follows WHERE ticket_id = @t", new { t }));
    }
}
