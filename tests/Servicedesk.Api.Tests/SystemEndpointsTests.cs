using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SystemEndpointsTests : IClassFixture<SecurityBaselineFactory>
{
    private readonly SecurityBaselineFactory _factory;

    public SystemEndpointsTests(SecurityBaselineFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SystemVersionEndpoint_ReturnsVersionShape()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<VersionPayload>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.Version));
        Assert.False(string.IsNullOrWhiteSpace(payload.Commit));
    }

    [Fact]
    public async Task SystemTimeEndpoint_ReturnsUtcAndOffset()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/time");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TimePayload>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.Timezone));
        Assert.True(payload.Utc != default);
        Assert.InRange(payload.OffsetMinutes, -14 * 60, 14 * 60);
    }

    private sealed record VersionPayload(string Version, string Commit, DateTimeOffset BuildTime);
    private sealed record TimePayload(DateTimeOffset Utc, string Timezone, int OffsetMinutes);
}
