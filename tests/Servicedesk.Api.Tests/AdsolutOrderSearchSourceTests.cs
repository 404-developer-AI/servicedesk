using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the authorization boundary of
/// <see cref="Infrastructure.Search.AdsolutOrderSearchSource"/>: customers see
/// nothing, agents/admins are eligible. The per-user feature-flag filter
/// (<c>adsolut_orders_enabled</c>) that yields zero hits for an agent without
/// the flag — and the admin display status filter — are enforced inside
/// SearchAsync via DB + settings reads, exercised against a real Postgres in
/// the integration pass. These unit-level tests cover the availability gate +
/// the no-DB short-circuits (passing null! deps proves they never open a
/// connection or read settings).
public sealed class AdsolutOrderSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new Infrastructure.Search.AdsolutOrderSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new Infrastructure.Search.AdsolutOrderSearchSource(null!, null!);
        var flaggedAgent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(),
            new HashSet<string> { SearchFeature.AdsolutOrders });
        var plainAgent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(flaggedAgent));
        Assert.True(src.IsAvailableFor(admin));
        // Without adsolut_orders_enabled the source is hidden from the dropdown.
        Assert.False(src.IsAvailableFor(plainAgent));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // null! deps: if the customer branch tried to open a connection or read
        // settings it would NRE — proving the no-access short-circuit.
        var src = new Infrastructure.Search.AdsolutOrderSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("bhk drees", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.AdsolutOrders, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.AdsolutOrderSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Kind_constant_is_stable()
    {
        Assert.Equal("adsolut-orders", SearchSourceKind.AdsolutOrders);
    }
}
