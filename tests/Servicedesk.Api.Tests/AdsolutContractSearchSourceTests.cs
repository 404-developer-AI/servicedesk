using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the authorization boundary of
/// <see cref="Infrastructure.Search.AdsolutContractSearchSource"/>: customers see
/// nothing, agents/admins are eligible. The per-user feature-flag filter
/// (<c>contracts_enabled</c>) that yields zero hits for an agent without the
/// flag is enforced inside SearchAsync via a DB read, exercised against a real
/// Postgres in the integration pass. These unit-level tests cover the
/// availability gate + the no-DB short-circuits (passing null! deps proves they
/// never open a connection or read settings).
public sealed class AdsolutContractSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new Infrastructure.Search.AdsolutContractSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new Infrastructure.Search.AdsolutContractSearchSource(null!, null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(agent));
        Assert.True(src.IsAvailableFor(admin));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // null! deps: if the customer branch tried to open a connection or read
        // settings it would NRE — proving the no-access short-circuit.
        var src = new Infrastructure.Search.AdsolutContractSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("hosting", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.AdsolutContracts, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.AdsolutContractSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Kind_constant_is_stable()
    {
        Assert.Equal("adsolut-contracts", SearchSourceKind.AdsolutContracts);
    }
}
