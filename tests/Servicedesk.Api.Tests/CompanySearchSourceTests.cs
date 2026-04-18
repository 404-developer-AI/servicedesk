using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.9 — verifies the principal-gating surface of
/// <see cref="Infrastructure.Search.CompanySearchSource"/>. The actual SQL
/// path needs a real Postgres to exercise; these tests pin down the
/// authorization boundary (customers see nothing) and the short-circuit on
/// empty queries, both of which should short-cut before any DB call.
public sealed class CompanySearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new Infrastructure.Search.CompanySearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new Infrastructure.Search.CompanySearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(agent));
        Assert.True(src.IsAvailableFor(admin));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // Passing null! as data source proves the customer branch short-circuits:
        // if SearchAsync tried to talk to Postgres it would NRE here.
        var src = new Infrastructure.Search.CompanySearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("acme", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.Companies, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.CompanySearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }
}
