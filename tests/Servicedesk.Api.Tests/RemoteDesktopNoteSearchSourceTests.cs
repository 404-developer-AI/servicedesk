using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the authorization boundary of <see cref="RemoteDesktopNoteSearchSource"/>
/// (v0.1.10): customers never see a hit, agents/admins are eligible. The
/// null! data source proves the customer and empty-query branches never
/// open a connection.
public sealed class RemoteDesktopNoteSearchSourceTests
{
    [Fact]
    public void Customer_principal_is_not_available()
    {
        var src = new RemoteDesktopNoteSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(principal));
    }

    [Fact]
    public void Agent_and_admin_principals_are_available()
    {
        var src = new RemoteDesktopNoteSearchSource(null!);

        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>())));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
    }

    [Fact]
    public async Task Customer_search_returns_empty_group_without_hitting_db()
    {
        var src = new RemoteDesktopNoteSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        var result = await src.SearchAsync(new SearchRequest("acme", null, 10, 0), principal, default);

        Assert.Equal(SearchSourceKind.RemoteDesktopNotes, result.Kind);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_query_returns_empty_group_without_hitting_db()
    {
        var src = new RemoteDesktopNoteSearchSource(null!);
        var principal = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        var result = await src.SearchAsync(new SearchRequest("   ", null, 10, 0), principal, default);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Kind_constant_is_stable()
    {
        Assert.Equal("remote-desktop-notes", SearchSourceKind.RemoteDesktopNotes);
    }
}
