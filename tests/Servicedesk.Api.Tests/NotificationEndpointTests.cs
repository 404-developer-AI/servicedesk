using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.12 stap 4 — confirms every /api/notifications route is gated by
/// RequireAgent before it can touch the repository. Customers (future
/// portal) must never receive 200 here, and unauthenticated callers hit
/// 401 before any DB connection is opened.
public sealed class NotificationEndpointTests : IClassFixture<SecurityBaselineFactory>
{
    private readonly SecurityBaselineFactory _factory;

    public NotificationEndpointTests(SecurityBaselineFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/notifications/pending")]
    [InlineData("/api/notifications/history")]
    [InlineData("/api/notifications/history?limit=25")]
    public async Task Get_routes_reject_unauthenticated(string url)
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Mark_viewed_rejects_unauthenticated()
    {
        using var client = _factory.CreateClient();
        var res = await client.PostAsync($"/api/notifications/{Guid.NewGuid()}/view", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Mark_acked_rejects_unauthenticated()
    {
        using var client = _factory.CreateClient();
        var res = await client.PostAsync($"/api/notifications/{Guid.NewGuid()}/ack", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Ack_all_rejects_unauthenticated()
    {
        using var client = _factory.CreateClient();
        var res = await client.PostAsync("/api/notifications/ack-all", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
