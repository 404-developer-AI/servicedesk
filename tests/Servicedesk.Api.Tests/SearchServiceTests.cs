using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.93 — pins the search façade's fan-out guarantees: the dropdown only
/// queries the quick-scoped sources, a source that exceeds its time budget is
/// cancelled and degrades to an empty group (instead of stalling the whole
/// request for up to the 30 s Npgsql command timeout, the pre-v0.0.93
/// behaviour), and a throwing source is isolated the same way.
public sealed class SearchServiceTests
{
    private static readonly SearchPrincipal Admin = new(Guid.NewGuid(), "Admin", null);

    [Fact]
    public async Task Kinds_filter_skips_sources_outside_the_quick_scope()
    {
        var tickets = new FakeSource("tickets");
        var erp = new FakeSource("adsolut-orders");
        var service = BuildService(tickets, erp);

        var results = await service.SearchAsync(
            new SearchRequest("datawolk", Type: null, Limit: 5, Offset: 0,
                Kinds: new[] { "tickets" }, QuickMode: true),
            Admin, default);

        Assert.True(tickets.WasCalled);
        Assert.False(erp.WasCalled);
        Assert.Single(results.Groups);
        Assert.Equal("tickets", results.Groups[0].Kind);
    }

    [Fact]
    public async Task Null_kinds_keeps_the_full_fanout()
    {
        var tickets = new FakeSource("tickets");
        var erp = new FakeSource("adsolut-orders");
        var service = BuildService(tickets, erp);

        await service.SearchAsync(
            new SearchRequest("datawolk", Type: null, Limit: 5, Offset: 0),
            Admin, default);

        Assert.True(tickets.WasCalled);
        Assert.True(erp.WasCalled);
    }

    [Fact]
    public async Task Slow_source_is_cut_off_and_returns_an_empty_group()
    {
        var fast = new FakeSource("tickets");
        var slow = new FakeSource("adsolut-orders") { Delay = TimeSpan.FromSeconds(30) };
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Search.SourceTimeoutMs, "300");
        var service = BuildService(settings, fast, slow);

        var results = await service.SearchAsync(
            new SearchRequest("datawolk", Type: null, Limit: 5, Offset: 0),
            Admin, default);

        var slowGroup = Assert.Single(results.Groups, g => g.Kind == "adsolut-orders");
        Assert.Empty(slowGroup.Hits);
        Assert.Equal(0, slowGroup.TotalInGroup);

        var fastGroup = Assert.Single(results.Groups, g => g.Kind == "tickets");
        Assert.Single(fastGroup.Hits);
        Assert.Equal(1, results.TotalHits);
    }

    [Fact]
    public async Task Throwing_source_is_isolated_to_an_empty_group()
    {
        var fast = new FakeSource("tickets");
        var broken = new FakeSource("contacts") { Throws = true };
        var service = BuildService(fast, broken);

        var results = await service.SearchAsync(
            new SearchRequest("datawolk", Type: null, Limit: 5, Offset: 0),
            Admin, default);

        var brokenGroup = Assert.Single(results.Groups, g => g.Kind == "contacts");
        Assert.Empty(brokenGroup.Hits);
        Assert.Equal(1, results.TotalHits);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        var slow = new FakeSource("tickets") { Delay = TimeSpan.FromSeconds(30) };
        var service = BuildService(slow);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync(
                new SearchRequest("datawolk", Type: null, Limit: 5, Offset: 0),
                Admin, cts.Token));
    }

    private static SearchService BuildService(params ISearchSource[] sources) =>
        BuildService(new InMemorySettingsService(), sources);

    private static SearchService BuildService(
        InMemorySettingsService settings, params ISearchSource[] sources) =>
        new(sources, settings, NullLogger<SearchService>.Instance);

    private sealed class FakeSource : ISearchSource
    {
        public FakeSource(string kind) => Kind = kind;

        public string Kind { get; }
        public bool WasCalled { get; private set; }
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;
        public bool Throws { get; init; }

        public bool IsAvailableFor(SearchPrincipal principal) => true;

        public async Task<SearchGroup> SearchAsync(
            SearchRequest request, SearchPrincipal principal, CancellationToken ct)
        {
            WasCalled = true;
            if (Throws) throw new InvalidOperationException("boom");
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            var hit = new SearchHit(Kind, Guid.NewGuid().ToString(), "hit", null, 1.0);
            return new SearchGroup(Kind, new[] { hit }, 1, false);
        }
    }
}
