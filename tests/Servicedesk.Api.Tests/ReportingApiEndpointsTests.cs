using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The Reporting API public surface (v0.0.96) is internet-facing and gated
/// by a pre-shared key plus an optional IP allow-list, not a session. These
/// tests pin the security gate: invisible (404) unless enabled AND a key is
/// configured AND the caller's IP passes the allow-list; 401 only for an
/// allowed caller with a wrong/missing key; and the admin config surface
/// stays session-gated. Real SQL needs Postgres and lives elsewhere; the
/// report service here is an empty fake so handler resolution succeeds.
public sealed class ReportingApiEndpointsTests
{
    private const string KeyHeader = "X-Reporting-Api-Key";
    private const string ApiKey = "test-reporting-key-1234567890-abc";
    private const string Url = "/api/reporting/tickets?from=2026-08-01&to=2026-09-01";

    [Fact]
    public async Task Public_surface_is_404_when_disabled()
    {
        using var factory = new SecurityBaselineFactory();
        // Reporting.Enabled defaults to false (seeded from SettingDefaults).
        using var client = factory.CreateClient();

        var res = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Public_surface_is_404_when_enabled_but_no_key_configured()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Reporting.Enabled, "true");
        using var client = factory.CreateClient();

        var res = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Public_surface_is_401_without_key_header()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Reporting.Enabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.ReportingApiKey, ApiKey);
        using var client = factory.CreateClient();

        var res = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Public_surface_is_401_with_wrong_key()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Reporting.Enabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.ReportingApiKey, ApiKey);
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Add(KeyHeader, "not-the-key");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Public_surface_is_404_when_ip_outside_allow_list_even_with_correct_key()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Reporting.Enabled, "true");
        factory.Settings.Set(SettingKeys.Reporting.IpAllowList, "203.0.113.0/24");
        factory.Secrets.Set(ProtectedSecretKeys.ReportingApiKey, ApiKey);
        using var client = factory.CreateClient();

        // The TestServer connection has no remote IP; with a non-empty
        // allow-list that must fail closed — and as a 404, not a 401/403,
        // so a non-allowed caller cannot even learn the surface exists.
        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Add(KeyHeader, ApiKey);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Public_surface_allows_correct_key()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Reporting.Enabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.ReportingApiKey, ApiKey);
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Add(KeyHeader, ApiKey);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ReportShape>();
        Assert.NotNull(body);
        Assert.NotNull(body!.Opened);
        Assert.NotNull(body.Closed);
        Assert.NotNull(body.OpenNow);
    }

    [Theory]
    [InlineData("/api/reporting/tickets")]
    [InlineData("/api/reporting/tickets?from=2026-08-01")]
    [InlineData("/api/reporting/tickets?from=not-a-date&to=2026-09-01")]
    [InlineData("/api/reporting/tickets?from=2026-09-01&to=2026-08-01")]
    public async Task Public_surface_rejects_invalid_period(string url)
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Reporting.Enabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.ReportingApiKey, ApiKey);
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add(KeyHeader, ApiKey);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Admin_config_requires_authentication()
    {
        using var factory = new SecurityBaselineFactory();
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/api/admin/reporting/status");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private sealed record ReportShape(SectionShape? Opened, SectionShape? Closed, SectionShape? OpenNow);
    private sealed record SectionShape(int Count, int Returned, int Offset, bool Truncated);
}
