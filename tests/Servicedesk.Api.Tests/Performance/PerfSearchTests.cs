using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests.Performance;

/// The Performance dashboard is reachable from global search through the
/// admin-only settings source; nobody else may see it.
public sealed class PerfSearchTests
{
    private static readonly SearchRequest Query = new("performance", null, 10, 0);

    [Fact]
    public async Task Admin_FindsThePerformancePage()
    {
        var source = new ScopedSearchSource(new SettingsSearchSource());
        var result = await source.SearchAsync(Query, new SearchPrincipal(Guid.NewGuid(), "Admin", null), default);
        Assert.Contains(result.Hits, h => h.Title == "Performance");
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Customer")]
    public async Task NonAdmins_GetZeroHits(string role)
    {
        var source = new ScopedSearchSource(new SettingsSearchSource());
        var result = await source.SearchAsync(Query, new SearchPrincipal(Guid.NewGuid(), role, Array.Empty<Guid>()), default);
        Assert.Empty(result.Hits);
    }
}
