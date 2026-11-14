using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Infrastructure.Access;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The queue-access list is cached per user for two minutes, while the role
/// is re-read on every request. A user demoted from Admin to Agent must get
/// their granted queues on the very next request — never the cached
/// "every queue" list of their admin days.
[Collection(PostgresCollection.Name)]
public sealed class QueueAccessCacheDbTests
{
    private readonly PostgresDatabase _db;

    public QueueAccessCacheDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Demoted_admin_never_gets_the_cached_admin_queue_list()
    {
        var seed = new DbSeed(_db.DataSource);
        var granted = await seed.QueueAsync("Granted");
        var other = await seed.QueueAsync("Other");
        var user = await seed.UserAsync("Agent");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO user_queue_access (user_id, queue_id) VALUES (@u, @q)";
            cmd.Parameters.AddWithValue("u", user);
            cmd.Parameters.AddWithValue("q", granted);
            await cmd.ExecuteNonQueryAsync();
        }

        var svc = new QueueAccessService(_db.DataSource, new MemoryCache(new MemoryCacheOptions()));

        // While still Admin: every active queue (and it gets cached).
        var asAdmin = await svc.GetAccessibleQueueIdsAsync(user, "Admin");
        Assert.Contains(other, asAdmin);

        // Next request after the demotion: only the granted queue.
        var asAgent = await svc.GetAccessibleQueueIdsAsync(user, "Agent");
        Assert.Contains(granted, asAgent);
        Assert.DoesNotContain(other, asAgent);
        Assert.False(await svc.HasQueueAccessAsync(user, "Agent", other));
    }

    [PostgresFact]
    public async Task Invalidate_drops_both_role_entries()
    {
        var seed = new DbSeed(_db.DataSource);
        var queue = await seed.QueueAsync();
        var user = await seed.UserAsync("Agent");
        var svc = new QueueAccessService(_db.DataSource, new MemoryCache(new MemoryCacheOptions()));

        Assert.DoesNotContain(queue, await svc.GetAccessibleQueueIdsAsync(user, "Agent"));
        await svc.SetQueueAccessAsync(user, new[] { queue });   // invalidates internally
        Assert.Contains(queue, await svc.GetAccessibleQueueIdsAsync(user, "Agent"));
    }
}
