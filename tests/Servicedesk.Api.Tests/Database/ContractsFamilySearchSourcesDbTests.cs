using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the Contracts-flag family beyond Adsolut: M365
/// mailboxes, Sophos tenants and report templates. Admin always passes, an
/// agent needs the `contracts` flag, Customer never.
[Collection(PostgresCollection.Name)]
public sealed class ContractsFamilySearchSourcesDbTests
{
    private readonly PostgresDatabase _db;

    public ContractsFamilySearchSourcesDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task M365_mailboxes_are_found_by_admin_and_contracts_flag_holders_only()
    {
        var token = Token();
        var company = await new DbSeed(_db.DataSource).CompanyAsync();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            await c.ExecuteAsync("""
                INSERT INTO m365_mailboxes (company_id, object_id, display_name, upn, mail)
                VALUES (@company, @objectId, @token, @upn, @upn)
                """,
                new { company, objectId = Guid.NewGuid().ToString(), token, upn = $"{token}@example.test" });

        var source = new M365MailboxSearchSource(_db.DataSource);
        var expected = new[] { company.ToString() };

        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.Contracts), default)));

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.Assets, SearchFeature.AdsolutTimesheet), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Contracts), token);
    }

    [PostgresFact]
    public async Task Sophos_tenants_are_found_by_admin_and_contracts_flag_holders_only_and_unlinked_tenants_are_hidden()
    {
        var token = Token();
        var company = await new DbSeed(_db.DataSource).CompanyAsync();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync(
                "INSERT INTO sophos_tenants (tenant_id, show_as, company_id) VALUES (@id, @token, @company)",
                new { id = Guid.NewGuid().ToString(), token, company });
            // Not yet linked to a local company: never searchable.
            await c.ExecuteAsync(
                "INSERT INTO sophos_tenants (tenant_id, show_as) VALUES (@id, @show)",
                new { id = Guid.NewGuid().ToString(), show = $"{token} unlinked" });
        }

        var source = new SophosTenantSearchSource(_db.DataSource);
        var expected = new[] { company.ToString() };

        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.Contracts), default)));

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.Assets), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Contracts), token);
    }

    [PostgresFact]
    public async Task Report_templates_are_found_by_admin_and_contracts_flag_holders_only()
    {
        var token = Token();
        Guid id;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            id = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO report_templates (name, description) VALUES (@name, 'Example report') RETURNING id",
                new { name = $"{token} monthly" });

        var source = new ReportTemplateSearchSource(_db.DataSource);

        Assert.Equal(Sorted(id), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(Sorted(id), Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.Contracts), default)));

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.AdsolutOrders), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Contracts), token);
    }
}
