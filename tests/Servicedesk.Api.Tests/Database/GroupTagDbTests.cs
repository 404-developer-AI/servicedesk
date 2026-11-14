using Dapper;
using Servicedesk.Infrastructure.TagGroups;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — group tags: one shared row per group mention. Members see it
/// until ONE of them acknowledges it; non-members never see or ack it; two
/// members acking at once have exactly one winner; inactive groups vanish.
[Collection(PostgresCollection.Name)]
public sealed class GroupTagDbTests
{
    private readonly PostgresDatabase _db;

    public GroupTagDbTests(PostgresDatabase db) => _db = db;

    private async Task<(Guid Ticket, long EventId, long Number)> TicketAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticket = await seed.TicketAsync("Printer offline", await seed.ContactAsync(), q, s, p);
        var evt = await seed.EventAsync(ticket, "Note", isInternal: true);
        await using var c = await _db.DataSource.OpenConnectionAsync();
        var number = await c.ExecuteScalarAsync<long>("SELECT number FROM tickets WHERE id = @ticket", new { ticket });
        return (ticket, evt, number);
    }

    private static NewGroupTag Tag(Guid group, (Guid Ticket, long EventId, long Number) t)
        => new(group, t.Ticket, t.Number, "Printer offline", t.EventId, "Note", null, "Can someone check this?");

    [PostgresFact]
    public async Task Members_share_one_tag_until_one_of_them_acknowledges_it()
    {
        var seed = new DbSeed(_db.DataSource);
        var a = await seed.UserAsync("Agent");
        var b = await seed.UserAsync("Agent");
        var outsider = await seed.UserAsync("Agent");
        var groups = new TagGroupRepository(_db.DataSource);
        var group = await groups.CreateAsync($"Back office {Guid.NewGuid():N}"[..20], true, new[] { a, b }, default);
        var tags = new GroupTagRepository(_db.DataSource);
        var tag = Assert.Single(await tags.CreateManyAsync(new[] { Tag(group, await TicketAsync()) }, default));

        Assert.Contains(await tags.ListPendingForUserAsync(a, default), t => t.Id == tag.Id);
        Assert.Contains(await tags.ListPendingForUserAsync(b, default), t => t.Id == tag.Id);
        Assert.DoesNotContain(await tags.ListPendingForUserAsync(outsider, default), t => t.Id == tag.Id);
        Assert.False(await tags.IsVisibleToAsync(tag.Id, outsider, default));

        Assert.Null(await tags.AckAsync(tag.Id, outsider, default)); // not a member
        Assert.Equal(group, await tags.AckAsync(tag.Id, b, default));

        Assert.DoesNotContain(await tags.ListPendingForUserAsync(a, default), t => t.Id == tag.Id);
        var history = await tags.ListHistoryForUserAsync(a, null, 50, default);
        var acked = Assert.Single(history, t => t.Id == tag.Id);
        Assert.NotNull(acked.AckedUtc);
        Assert.Empty(await tags.ListHistoryForUserAsync(outsider, null, 50, default));
    }

    [PostgresFact]
    public async Task Two_members_acking_at_once_have_exactly_one_winner()
    {
        var seed = new DbSeed(_db.DataSource);
        var a = await seed.UserAsync("Agent");
        var b = await seed.UserAsync("Agent");
        var groups = new TagGroupRepository(_db.DataSource);
        var group = await groups.CreateAsync($"Race {Guid.NewGuid():N}"[..20], true, new[] { a, b }, default);
        var tags = new GroupTagRepository(_db.DataSource);
        var tag = Assert.Single(await tags.CreateManyAsync(new[] { Tag(group, await TicketAsync()) }, default));

        var results = await Task.WhenAll(tags.AckAsync(tag.Id, a, default), tags.AckAsync(tag.Id, b, default));

        Assert.Single(results, r => r is not null);
    }

    [PostgresFact]
    public async Task Inactive_groups_are_not_resolved_and_their_tags_disappear()
    {
        var seed = new DbSeed(_db.DataSource);
        var a = await seed.UserAsync("Agent");
        var groups = new TagGroupRepository(_db.DataSource);
        var name = $"Sleepy {Guid.NewGuid():N}"[..20];
        var group = await groups.CreateAsync(name, true, new[] { a }, default);
        var tags = new GroupTagRepository(_db.DataSource);
        var tag = Assert.Single(await tags.CreateManyAsync(new[] { Tag(group, await TicketAsync()) }, default));

        Assert.True(await groups.UpdateAsync(group, name, false, new[] { a }, default));

        Assert.Empty(await groups.ResolveActiveByIdsAsync(new[] { group }, default));
        Assert.DoesNotContain(await tags.ListPendingForUserAsync(a, default), t => t.Id == tag.Id);
        Assert.False(await groups.IsMemberOfAnyActiveAsync(a, default));
    }

    [PostgresFact]
    public async Task Members_are_agents_only_and_deleting_a_group_cascades()
    {
        var seed = new DbSeed(_db.DataSource);
        var agent = await seed.UserAsync("Agent");
        var customer = await seed.UserAsync("Customer");
        var groups = new TagGroupRepository(_db.DataSource);
        var group = await groups.CreateAsync($"Mixed {Guid.NewGuid():N}"[..20], true, new[] { agent, customer }, default);

        var loaded = await groups.GetAsync(group, default);
        Assert.Equal(new[] { agent }, loaded!.Members.Select(m => m.UserId));

        var tags = new GroupTagRepository(_db.DataSource);
        var tag = Assert.Single(await tags.CreateManyAsync(new[] { Tag(group, await TicketAsync()) }, default));
        Assert.True(await groups.DeleteAsync(group, default));

        await using var c = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM group_tags WHERE id = @id", new { id = tag.Id }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM tag_group_members WHERE group_id = @group", new { group }));
    }

    [PostgresFact]
    public async Task Member_added_later_sees_the_open_tags_and_a_removed_one_does_not()
    {
        var seed = new DbSeed(_db.DataSource);
        var a = await seed.UserAsync("Agent");
        var late = await seed.UserAsync("Agent");
        var groups = new TagGroupRepository(_db.DataSource);
        var name = $"Shift {Guid.NewGuid():N}"[..20];
        var group = await groups.CreateAsync(name, true, new[] { a }, default);
        var tags = new GroupTagRepository(_db.DataSource);
        var tag = Assert.Single(await tags.CreateManyAsync(new[] { Tag(group, await TicketAsync()) }, default));

        await groups.UpdateAsync(group, name, true, new[] { late }, default);

        Assert.Contains(await tags.ListPendingForUserAsync(late, default), t => t.Id == tag.Id);
        Assert.DoesNotContain(await tags.ListPendingForUserAsync(a, default), t => t.Id == tag.Id);
    }
}
