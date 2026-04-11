using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.5 taxonomy endpoints sit behind <c>RequireAdmin</c>. These tests
/// verify every route is registered, mounted on the right group, and
/// rejects unauthenticated callers with 401 before the handler (and its
/// database-bound repository) runs. Full CRUD exercises need a real
/// Postgres instance and live in the integration test project.
public sealed class TaxonomyEndpointsTests : IClassFixture<SecurityBaselineFactory>
{
    private readonly SecurityBaselineFactory _factory;

    public TaxonomyEndpointsTests(SecurityBaselineFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/taxonomy/queues")]
    [InlineData("/api/taxonomy/priorities")]
    [InlineData("/api/taxonomy/statuses")]
    [InlineData("/api/taxonomy/categories")]
    public async Task Listing_is_admin_only(string url)
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/companies")]
    [InlineData("/api/contacts")]
    public async Task Customer_endpoints_are_admin_only(string url)
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Tickets_listing_requires_agent_or_admin()
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Dev_benchmark_endpoints_do_not_exist_in_production()
    {
        // The factory sets environment to "Production". The benchmark group is
        // only mounted in Development, so these routes must 404 — proving the
        // guard keeps seeders out of production builds.
        using var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/dev/benchmarks");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
