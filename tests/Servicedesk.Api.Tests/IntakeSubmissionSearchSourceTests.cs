using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.19 — pins the authorization boundary of
/// <see cref="Infrastructure.Search.IntakeSubmissionSearchSource"/>:
/// customers get zero hits; agents with no accessible queues also get zero
/// hits (before a DB round-trip). Passing null! as the data source proves
/// each short-circuit path.
public sealed class IntakeSubmissionSearchSourceTests
{
    [Fact]
    public void Customer_is_not_available()
    {
        var src = new Infrastructure.Search.IntakeSubmissionSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_are_available()
    {
        var src = new Infrastructure.Search.IntakeSubmissionSearchSource(null!);

        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>())));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.IntakeSubmissionSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(
            new SearchRequest("laptop", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.IntakeSubmissions, result.Kind);
        Assert.Empty(result.Hits);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Agent_with_no_queues_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.IntakeSubmissionSearchSource(null!);
        // Zero allowed queues — the agent can't see any submission; must
        // short-circuit before the SQL runs so the null! DataSource is never
        // dereferenced.
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());

        var result = await src.SearchAsync(
            new SearchRequest("serienummer", null, 10, 0), agent, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new Infrastructure.Search.IntakeSubmissionSearchSource(null!);
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(
            new SearchRequest("  ", null, 10, 0), admin, default);

        Assert.Empty(result.Hits);
    }
}
