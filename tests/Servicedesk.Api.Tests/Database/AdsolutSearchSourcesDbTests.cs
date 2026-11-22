using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the four Adsolut mirror sources. The mirrors
/// are install-wide, so the boundary is the per-user feature flag (Admin
/// always passes, Customer never) plus, for contracts and orders, the admin's
/// display status filter.
[Collection(PostgresCollection.Name)]
public sealed class AdsolutSearchSourcesDbTests
{
    private readonly PostgresDatabase _db;

    public AdsolutSearchSourcesDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Articles_are_found_by_admin_and_contracts_flag_holders_only()
    {
        var token = Token();
        var id = Guid.NewGuid();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync(
                "INSERT INTO adsolut_articles (id, code, name, description) VALUES (@id, @code, 'Example article', 'Example text')",
                new { id, code = token });

        var source = new AdsolutArticleSearchSource(_db.DataSource);

        Assert.Equal(Sorted(id), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(id), Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.Contracts), default)));

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.AdsolutOrders, SearchFeature.Assets), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Contracts), token);
    }

    [PostgresFact]
    public async Task Contracts_follow_the_flag_and_the_status_filter()
    {
        var token = Token();
        var id = Guid.NewGuid();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("""
                INSERT INTO adsolut_contracts (id, customer_name, description, state_code)
                VALUES (@id, 'Example Company', @description, 'ACTIVE')
                """,
                new { id, description = token });

        var open = new AdsolutContractSearchSource(_db.DataSource, new FixedStringSettings(""));
        Assert.Equal(Sorted(id), Ids(await open.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(id), Ids(await open.SearchAsync(Query(token), Agent(SearchFeature.Contracts), default)));

        await AssertNoHitsAsync(open, Agent(), token);
        await AssertNoHitsAsync(open, Agent(SearchFeature.AdsolutOrders), token);
        await AssertNoHitsAsync(open, Customer(SearchFeature.Contracts), token);

        var matching = new AdsolutContractSearchSource(_db.DataSource, new FixedStringSettings("ACTIVE, STOPPED"));
        Assert.Equal(Sorted(id), Ids(await matching.SearchAsync(Query(token), Admin(), default)));

        // A status outside the admin's display filter is hidden, even from admins.
        var excluding = new AdsolutContractSearchSource(_db.DataSource, new FixedStringSettings("STOPPED"));
        Assert.Empty((await excluding.SearchAsync(Query(token), Admin(), default)).Hits);
        Assert.Empty((await excluding.SearchAsync(Query(token), Agent(SearchFeature.Contracts), default)).Hits);
    }

    [PostgresFact]
    public async Task Orders_follow_the_flag_and_the_status_filter()
    {
        var token = Token();
        var id = Guid.NewGuid();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("""
                INSERT INTO adsolut_orders (id, customer_name, remark, state_code)
                VALUES (@id, 'Example Company', @remark, 'OPEN')
                """,
                new { id, remark = token });

        var open = new AdsolutOrderSearchSource(_db.DataSource, new FixedStringSettings(""));
        Assert.Equal(Sorted(id), Ids(await open.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(id), Ids(await open.SearchAsync(Query(token), Agent(SearchFeature.AdsolutOrders), default)));

        await AssertNoHitsAsync(open, Agent(), token);
        await AssertNoHitsAsync(open, Agent(SearchFeature.Contracts, SearchFeature.AdsolutTimesheet), token);
        await AssertNoHitsAsync(open, Customer(SearchFeature.AdsolutOrders), token);

        var excluding = new AdsolutOrderSearchSource(_db.DataSource, new FixedStringSettings("CLOSED"));
        Assert.Empty((await excluding.SearchAsync(Query(token), Admin(), default)).Hits);
        Assert.Empty((await excluding.SearchAsync(Query(token), Agent(SearchFeature.AdsolutOrders), default)).Hits);
    }

    [PostgresFact]
    public async Task Sales_receipts_are_found_by_admin_and_timesheet_flag_holders_only()
    {
        var token = Token();
        var id = Guid.NewGuid();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("""
                INSERT INTO adsolut_sales_receipts (id, customer_name, description)
                VALUES (@id, 'Example Company', @description)
                """,
                new { id, description = token });

        var source = new AdsolutSalesReceiptSearchSource(_db.DataSource);

        Assert.Equal(Sorted(id), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(id), Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.AdsolutTimesheet), default)));

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.Contracts, SearchFeature.AdsolutOrders), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.AdsolutTimesheet), token);
    }
}
