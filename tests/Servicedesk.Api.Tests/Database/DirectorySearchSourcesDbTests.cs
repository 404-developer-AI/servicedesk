using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the people/organisation sources: companies,
/// contacts and portal accounts are visible to every Agent and Admin (not
/// queue-scoped, no feature flag), never to Customers, and deactivated
/// companies/contacts never surface.
[Collection(PostgresCollection.Name)]
public sealed class DirectorySearchSourcesDbTests
{
    private readonly PostgresDatabase _db;

    public DirectorySearchSourcesDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Companies_are_found_by_agents_and_admins_and_inactive_ones_are_hidden()
    {
        var token = Token();
        var seed = new DbSeed(_db.DataSource);
        var active = await seed.CompanyAsync(token);
        var inactive = await seed.CompanyAsync(token);
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE companies SET is_active = FALSE WHERE id = @inactive", new { inactive });

        var source = new CompanySearchSource(_db.DataSource);

        Assert.Equal(Sorted(active), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(active), Ids(await source.SearchAsync(Query(token), Agent(), default)));

        await AssertNoHitsAsync(source, Customer(), token);
    }

    [PostgresFact]
    public async Task Contacts_are_found_by_agents_and_admins_and_inactive_ones_are_hidden()
    {
        var token = Token();
        Guid active, inactive;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            active = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO contacts (email, first_name, last_name) VALUES (@email, 'Jane', 'Example') RETURNING id",
                new { email = $"{token}@example.test" });
            inactive = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO contacts (email, first_name, last_name, is_active) VALUES (@email, 'Joe', 'Example', FALSE) RETURNING id",
                new { email = $"{token}.old@example.test" });
        }

        var source = new ContactSearchSource(_db.DataSource, new NoPhoneNormalizer());

        Assert.Equal(Sorted(active), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(active), Ids(await source.SearchAsync(Query(token), Agent(), default)));
        Assert.DoesNotContain(inactive.ToString(), Ids(await source.SearchAsync(Query(token), Admin(), default)));

        await AssertNoHitsAsync(source, Customer(), token);
    }

    [PostgresFact]
    public async Task Portal_accounts_are_found_by_agents_and_admins_but_never_by_customers()
    {
        var token = Token();
        var userId = await new DbSeed(_db.DataSource).UserAsync("Customer");
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("""
                INSERT INTO portal_accounts (user_id, status, display_name, origin)
                VALUES (@userId, 'Active', @name, 'Registration')
                """,
                new { userId, name = $"{token} Example" });

        var source = new PortalAccountSearchSource(_db.DataSource);

        Assert.Equal(Sorted(userId), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(userId), Ids(await source.SearchAsync(Query(token), Agent(), default)));

        await AssertNoHitsAsync(source, Customer(), token);
    }
}
