using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.58 — pins the authorization boundary of
/// <see cref="Infrastructure.Search.SignatureSearchSource"/>: only admins can
/// ever see signature hits in global search. Passing <c>null!</c> as the data
/// source proves every unauthorized-principal branch short-circuits BEFORE a
/// DB round-trip — a regression that opened a connection would NRE here.
public sealed class SignatureSearchSourceTests
{
    [Fact]
    public void Admin_is_the_only_principal_with_availability()
    {
        var src = new Infrastructure.Search.SignatureSearchSource(null!);

        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>())));
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Customer", null)));
    }

    [Fact]
    public async Task Agent_search_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.SignatureSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());

        var result = await src.SearchAsync(
            new SearchRequest("datawolk", null, 10, 0), agent, default);

        Assert.Equal(SearchSourceKind.Signatures, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.SignatureSearchSource(null!);
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("signature", null, 10, 0), customer, default);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.SignatureSearchSource(null!);
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), admin, default);

        Assert.Empty(result.Hits);
    }
}
