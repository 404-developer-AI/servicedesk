using System.Reflection;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// Global search row-level authorization for tickets (CLAUDE.md → Global
/// search): an agent only finds tickets in queues they have access to, an
/// agent without queues finds nothing, an admin finds everything. Runs the
/// real search SQL.
[Collection(PostgresCollection.Name)]
public sealed class TicketSearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public TicketSearchSourceDbTests(PostgresDatabase db) => _db = db;

    // Settings reads fail → TicketSearchSource falls back to its defaults.
    private TicketSearchSource Source => new(_db.DataSource, ThrowingSettings.Create());
    private DbSeed Seed => new(_db.DataSource);

    private static SearchRequest Query(string q) => new(q, Type: null, Limit: 50, Offset: 0);

    [PostgresFact]
    public async Task Agent_finds_tickets_only_in_their_queues_admin_finds_all()
    {
        var token = $"zebra{Guid.NewGuid():N}"[..14];
        var (queueA, s, p) = await Seed.TaxonomyAsync();
        var queueB = await Seed.QueueAsync("Other queue");
        var contact = await Seed.ContactAsync();
        var inA = await Seed.TicketAsync($"{token} printer", contact, queueA, s, p);
        var inB = await Seed.TicketAsync($"{token} laptop", contact, queueB, s, p);

        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", new[] { queueA });
        var agentHits = await Source.SearchAsync(Query(token), agent, default);
        Assert.Equal(new[] { inA.ToString() }, agentHits.Hits.Select(h => h.EntityId).ToArray());

        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", allowedQueueIds: null);
        var adminHits = await Source.SearchAsync(Query(token), admin, default);
        Assert.Equal(
            new[] { inA.ToString(), inB.ToString() }.Order().ToArray(),
            adminHits.Hits.Select(h => h.EntityId).Order().ToArray());
    }

    [PostgresFact]
    public async Task Agent_without_queues_and_customers_get_zero_hits()
    {
        var token = $"yak{Guid.NewGuid():N}"[..12];
        var (q, s, p) = await Seed.TaxonomyAsync();
        await Seed.TicketAsync($"{token} router", await Seed.ContactAsync(), q, s, p);

        var noQueues = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        Assert.Empty((await Source.SearchAsync(Query(token), noQueues, default)).Hits);

        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", new[] { q });
        Assert.Empty((await Source.SearchAsync(Query(token), customer, default)).Hits);
    }

    private class ThrowingSettings : DispatchProxy
    {
        public static ISettingsService Create() => Create<ISettingsService, ThrowingSettings>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("settings unavailable in this test");
    }
}
