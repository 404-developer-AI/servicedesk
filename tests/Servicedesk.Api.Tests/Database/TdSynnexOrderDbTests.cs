using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Integrations.TdSynnex;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.59 — Order Management against a real database: the mirror round-trip
/// the diff relies on, the dashboard buckets, ticket links filtered by queue
/// access (with a hidden count) and the flag-gated search source. Order data
/// is fictitious.
[Collection(PostgresCollection.Name)]
public sealed class TdSynnexOrderDbTests
{
    private readonly PostgresDatabase _db;

    public TdSynnexOrderDbTests(PostgresDatabase db) => _db = db;

    private static TdSynnexOrder Order(string number, string status, DateOnly? shipDate, string? po = null) => new()
    {
        OrderNumber = number,
        CustomerOrderNumber = po,
        Status = status,
        CreatedUtc = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
        TotalNet = 100m,
        Currency = "EUR",
        ShipTo = new TdSynnexShipTo { CompanyName = "Example Ltd" },
        Lines = new[]
        {
            new TdSynnexOrderLine
            {
                LineId = "10",
                ManufacturerPartNumber = "MPN-" + number,
                Quantity = 2,
                Splits = new[]
                {
                    new TdSynnexLineSplit { Quantity = 2, ShipDate = shipDate, Status = "OPEN" },
                },
            },
        },
    };

    private async Task<Guid> UpsertAsync(TdSynnexOrderRepository repo, TdSynnexOrder o) =>
        await repo.UpsertAsync(o, TdSynnexOrderDiff.Derive(o), changed: false, DateTime.UtcNow, default);

    [PostgresFact]
    public async Task Stored_order_round_trips_so_an_unchanged_order_is_no_change()
    {
        var repo = new TdSynnexOrderRepository(_db.DataSource);
        var number = $"I{Guid.NewGuid():N}"[..12];
        var order = Order(number, "OPEN", new DateOnly(2026, 11, 27));
        await UpsertAsync(repo, order);

        var stored = (await repo.GetStoredAsync(new[] { number }, default))[number];

        Assert.Equal(TdSynnexOrderDiff.Hash(order), stored.SnapshotHash);
        Assert.Equal(TdSynnexOrderDiff.Hash(order), TdSynnexOrderDiff.Hash(stored.Order));
        Assert.Empty(TdSynnexOrderDiff.Compare(stored.Order, order));
        Assert.NotEmpty(TdSynnexOrderDiff.Compare(stored.Order, order with { Status = "SHIPPING" }));
    }

    [PostgresFact]
    public async Task Late_and_due_buckets_follow_the_open_ship_date()
    {
        var repo = new TdSynnexOrderRepository(_db.DataSource);
        var tag = $"bucket{Guid.NewGuid():N}"[..14];
        var today = new DateOnly(2026, 11, 20);
        var late = await UpsertAsync(repo, Order($"L{Guid.NewGuid():N}"[..12], "OPEN", today.AddDays(-2), tag));
        var due = await UpsertAsync(repo, Order($"D{Guid.NewGuid():N}"[..12], "SHIPPING", today.AddDays(3), tag));
        var done = await UpsertAsync(repo, Order($"C{Guid.NewGuid():N}"[..12], "COMPLETED", today.AddDays(-9), tag));

        async Task<Guid[]> Ids(string bucket) =>
            (await repo.ListAsync(tag, bucket, today, null, null, 1, 50, default)).Items.Select(i => i.Id).ToArray();

        Assert.Equal(new[] { late }, await Ids(TdSynnexOrderBucket.Late));
        Assert.Equal(new[] { due }, await Ids(TdSynnexOrderBucket.Due));
        Assert.Equal(new[] { done }, await Ids(TdSynnexOrderBucket.Completed));
        Assert.Equal(2, (await Ids(TdSynnexOrderBucket.Active)).Length);
    }

    [PostgresFact]
    public async Task Linked_tickets_are_filtered_by_queue_access_with_a_hidden_count()
    {
        var repo = new TdSynnexOrderRepository(_db.DataSource);
        var seed = new DbSeed(_db.DataSource);
        var (queueA, status, priority) = await seed.TaxonomyAsync();
        var queueB = await seed.QueueAsync("Other queue");
        var contact = await seed.ContactAsync();
        var visible = await seed.TicketAsync("Laptop order", contact, queueA, status, priority);
        var hidden = await seed.TicketAsync("Server order", contact, queueB, status, priority);
        var orderId = await UpsertAsync(repo, Order($"K{Guid.NewGuid():N}"[..12], "OPEN", null));

        Assert.True(await repo.LinkAsync(visible, orderId, null, default));
        Assert.False(await repo.LinkAsync(visible, orderId, null, default)); // idempotent
        Assert.True(await repo.LinkAsync(hidden, orderId, null, default));

        var agentScope = new QueueAccessScope(false, new HashSet<Guid> { queueA });
        var linked = await repo.GetLinkedTicketsAsync(orderId, agentScope, default);
        Assert.Equal(new[] { visible }, linked.Visible.Select(t => t.TicketId).ToArray());
        Assert.Equal(1, linked.HiddenCount);

        var all = await repo.GetLinkedTicketsAsync(orderId, QueueAccessScope.AdminScope, default);
        Assert.Equal(2, all.Visible.Count);
        Assert.Equal(0, all.HiddenCount);
        Assert.Equal(2, (await repo.GetLinkedTicketIdsAsync(orderId, default)).Count);

        var forTicket = await repo.ListForTicketAsync(visible, 5, default);
        Assert.Equal(orderId, Assert.Single(forTicket).Header.Id);

        Assert.True(await repo.UnlinkAsync(visible, orderId, default));
        Assert.Empty(await repo.ListForTicketAsync(visible, 5, default));
    }

    [PostgresFact]
    public async Task Search_finds_orders_for_flag_holders_only()
    {
        var repo = new TdSynnexOrderRepository(_db.DataSource);
        var number = $"S{Guid.NewGuid():N}"[..12];
        await UpsertAsync(repo, Order(number, "OPEN", null, "PO-SEARCH"));
        var source = new TdSynnexOrderSearchSource(_db.DataSource);
        var request = new SearchRequest(number, null, 10, 0);

        var flagged = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(),
            new HashSet<string> { SearchFeature.OrderManagement });
        var plainAdmin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.Single((await source.SearchAsync(request, flagged, default)).Hits);
        Assert.Empty((await source.SearchAsync(request, plainAdmin, default)).Hits);
    }

    [PostgresFact]
    public async Task Change_history_is_kept_and_purged()
    {
        var repo = new TdSynnexOrderRepository(_db.DataSource);
        var orderId = await UpsertAsync(repo, Order($"H{Guid.NewGuid():N}"[..12], "OPEN", null));
        var changes = new[] { new TdSynnexOrderChange(TdSynnexChangeKind.Status, "Status OPEN → SHIPPING", "OPEN", "SHIPPING") };
        await repo.AddChangeAsync(orderId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "old", changes, default);
        await repo.AddChangeAsync(orderId, DateTime.UtcNow, "new", changes, default);

        await repo.PurgeChangesAsync(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), default);

        var detail = await repo.GetDetailAsync(orderId, 10, default);
        Assert.Equal("new", Assert.Single(detail!.Changes).Summary);
    }
}
