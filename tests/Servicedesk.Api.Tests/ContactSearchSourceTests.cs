using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.10 — pins the authorization boundary of
/// <see cref="Infrastructure.Search.ContactSearchSource"/>: customers see
/// nothing, agents and admins do. The SQL path itself needs a real Postgres
/// (pg_trgm) to exercise; these tests cover the principal-gate and the
/// empty-query short-circuit, both of which must short-cut before any DB call.
public sealed class ContactSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new Infrastructure.Search.ContactSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new Infrastructure.Search.ContactSearchSource(null!, null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(agent));
        Assert.True(src.IsAvailableFor(admin));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // Passing null! as data source proves the customer branch short-circuits:
        // if SearchAsync tried to open a connection it would NRE here.
        var src = new Infrastructure.Search.ContactSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("alice", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.Contacts, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.ContactSearchSource(null!, null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Theory]
    [InlineData("+32498123456",     true)]
    [InlineData("+1 212 555 1234",  true)]
    [InlineData("0498 12 34 56",    true)]
    [InlineData("0498123456",       true)]
    [InlineData("alice",            false)]
    [InlineData("alice@example.com", false)]
    [InlineData("32498",            false)]   // 5 digits, below threshold
    [InlineData("",                 false)]
    [InlineData("   ",              false)]
    [InlineData("john doe",         true)]    // 8 chars, no @ — fuzzy gate lets this in; normalizer will reject as invalid
    public void LooksLikePhone_gates_phone_branch(string input, bool expected)
    {
        Assert.Equal(expected, Infrastructure.Search.ContactSearchSource.LooksLikePhone(input));
    }
}
