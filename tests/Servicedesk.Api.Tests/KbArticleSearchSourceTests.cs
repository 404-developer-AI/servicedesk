using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.31 — pins the principal-gating surface of
/// <see cref="KbArticleSearchSource"/>. The SQL path needs a real Postgres
/// to exercise; these tests cover the row-level authorization (Customer
/// principal yields zero hits) and the empty-query short-circuit, both of
/// which must run before any DB call. Mirrors CompanySearchSourceTests.
public sealed class KbArticleSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new KbArticleSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new KbArticleSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.True(src.IsAvailableFor(agent));
        Assert.True(src.IsAvailableFor(admin));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        // Passing null! as data source proves the customer branch short-
        // circuits: if SearchAsync tried to talk to Postgres it would NRE.
        var src = new KbArticleSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("how to reset", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.KbArticles, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new KbArticleSearchSource(null!);
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("   ", null, 10, 0), admin, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public async Task Whitespace_query_short_circuits_for_agent_too()
    {
        var src = new KbArticleSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());

        var result = await src.SearchAsync(
            new SearchRequest("\t\n", null, 25, 0), agent, default);

        Assert.Equal(SearchSourceKind.KbArticles, result.Kind);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Kind_constant_matches_design_doc()
    {
        // The frontend searchMeta.ts is keyed off this exact string. If the
        // backend constant changes the FE labels stop matching silently.
        var src = new KbArticleSearchSource(null!);
        Assert.Equal("kb-articles", src.Kind);
    }
}
