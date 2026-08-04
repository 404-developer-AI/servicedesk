using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
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
        // Exact default values are asserted against SettingDefaults below;
        // the shared factory means another test may have overridden them here.
        Assert.Contains(payload.UpdateRefreshMode, new[] { "auto", "banner" });
    }

    [Fact]
    public async Task SystemVersionEndpoint_HonorsUpdateRefreshSettings()
    {
        _factory.Settings.Set(SettingKeys.App.UpdateRefreshMode, "banner");
        _factory.Settings.Set(SettingKeys.App.UpdateCheckOnFocus, "false");
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<VersionPayload>();
        Assert.NotNull(payload);
        Assert.Equal("banner", payload!.UpdateRefreshMode);
        Assert.False(payload.UpdateCheckOnFocus);
    }

    [Fact]
    public void UpdateRefreshSettings_HaveRegisteredDefaults()
    {
        var mode = SettingDefaults.All.Single(d => d.Key == SettingKeys.App.UpdateRefreshMode);
        Assert.Equal("auto", mode.Value);

        var focus = SettingDefaults.All.Single(d => d.Key == SettingKeys.App.UpdateCheckOnFocus);
        Assert.Equal("true", focus.Value);
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

    [Fact]
    public async Task SystemTimeEndpoint_HonorsAppTimeZoneSetting()
    {
        _factory.Settings.Set(SettingKeys.App.TimeZone, "UTC");
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/time");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TimePayload>();
        Assert.NotNull(payload);
        Assert.Equal("UTC", payload!.Timezone);
        Assert.Equal(0, payload.OffsetMinutes);
    }

    [Fact]
    public async Task SystemTimeEndpoint_InvalidTimeZoneFallsBackToLocal()
    {
        _factory.Settings.Set(SettingKeys.App.TimeZone, "Not/A-Real-Zone");
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/time");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TimePayload>();
        Assert.NotNull(payload);
        Assert.NotEqual("Not/A-Real-Zone", payload!.Timezone);
        Assert.InRange(payload.OffsetMinutes, -14 * 60, 14 * 60);
    }

    private sealed record VersionPayload(
        string Version, string Commit, DateTimeOffset BuildTime,
        string UpdateRefreshMode, bool UpdateCheckOnFocus);
    private sealed record TimePayload(DateTimeOffset Utc, string Timezone, int OffsetMinutes);
}
