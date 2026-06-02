using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the authorization boundary of
/// <see cref="Infrastructure.Search.AdsolutSalesReceiptSearchSource"/>:
/// customers see nothing, agents/admins are eligible. The per-user
/// feature-flag filter (<c>adsolut_timesheet_enabled</c>) that yields zero
/// hits for an agent without the flag is enforced inside SearchAsync via a
/// DB point lookup — exercised against a real Postgres in the integration
/// pass. These unit-level tests cover the availability gate + the no-DB
/// short-circuits (passing null! as the data source proves they never open a
/// connection).
public sealed class AdsolutSalesReceiptSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new Infrastructure.Search.AdsolutSalesReceiptSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new Infrastructure.Search.AdsolutSalesReceiptSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(agent));
        Assert.True(src.IsAvailableFor(admin));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // null! data source: if the customer branch tried to open a
        // connection it would NRE — proving the no-access short-circuit.
        var src = new Infrastructure.Search.AdsolutSalesReceiptSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("raidho", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.AdsolutSalesReceipts, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.AdsolutSalesReceiptSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Kind_constant_is_stable()
    {
        Assert.Equal("adsolut-sales-receipts", SearchSourceKind.AdsolutSalesReceipts);
    }
}
