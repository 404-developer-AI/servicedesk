using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Portal;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.0 — authorization boundary of the portal-accounts search source:
/// agents + admins only; a Customer principal gets zero hits BEFORE any DB
/// round-trip (null data source would NRE otherwise). Customers have no
/// global search at all, but the source must still hold on its own.
public sealed class PortalAccountSearchSourceTests
{
    [Fact]
    public void Agents_and_admins_available_customers_not()
    {
        var src = new PortalAccountSearchSource(null!);

        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>())));
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Customer", null)));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        var src = new PortalAccountSearchSource(null!);
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(new SearchRequest("someone@example.com", null, 10, 0), customer, default);

        Assert.Equal(SearchSourceKind.PortalAccounts, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new PortalAccountSearchSource(null!);
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(new SearchRequest("   ", null, 10, 0), admin, default);

        Assert.Empty(result.Hits);
    }
}
