using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.35 commit H — pins the authorization boundary of
/// <see cref="Infrastructure.Search.TimesheetSearchSource"/>: customers see
/// nothing, agents and admins are eligible. The row-level filter
/// (own-rows vs. all-rows based on the <c>timesheet_manager</c> flag) is
/// enforced inside the SQL — exercised against a real Postgres in the
/// integration-test pass, not here. These unit-level tests cover the
/// availability gate + the no-DB short-circuits.
public sealed class TimesheetSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new Infrastructure.Search.TimesheetSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new Infrastructure.Search.TimesheetSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(agent));
        Assert.True(src.IsAvailableFor(admin));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // Passing null! as data source proves the customer branch short-
        // circuits: if SearchAsync tried to open a connection it would NRE.
        var src = new Infrastructure.Search.TimesheetSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("vergadering", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.Timesheet, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.TimesheetSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Kind_constant_is_stable()
    {
        // Public contract for the frontend route + icon mapping. Bumping
        // this is a breaking change for any SPA build still in flight.
        Assert.Equal("timesheet", SearchSourceKind.Timesheet);
    }
}
