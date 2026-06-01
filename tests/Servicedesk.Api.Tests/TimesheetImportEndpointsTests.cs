using System.Net;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The migration import surface (v0.0.54) is internet-facing and gated by a
/// pre-shared token, not a session. These tests pin the security gate:
/// invisible (404) unless enabled AND a token is configured, 401 on a
/// wrong/missing token, and the admin config surface stays session-gated.
/// Full import behaviour needs a real Postgres and lives elsewhere; here the
/// import service is a no-op fake so handler resolution succeeds.
public sealed class TimesheetImportEndpointsTests
{
    private const string TokenHeader = "X-Timesheet-Import-Token";
    private const string Token = "test-import-token-1234567890-abc";

    [Theory]
    [InlineData("/api/timesheet/import/tasks")]
    [InlineData("/api/timesheet/import/users")]
    public async Task Migration_surface_is_404_when_disabled(string url)
    {
        using var factory = new SecurityBaselineFactory();
        // ImportEnabled defaults to false (seeded from SettingDefaults).
        using var client = factory.CreateClient();

        var res = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Migration_surface_is_404_when_enabled_but_no_token_configured()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Timesheet.ImportEnabled, "true");
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/api/timesheet/import/tasks");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Migration_surface_is_401_without_token_header()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Timesheet.ImportEnabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.TimesheetImportToken, Token);
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/api/timesheet/import/tasks");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Migration_surface_is_401_with_wrong_token()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Timesheet.ImportEnabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.TimesheetImportToken, Token);
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/timesheet/import/tasks");
        req.Headers.Add(TokenHeader, "not-the-token");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Migration_surface_allows_correct_token()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Timesheet.ImportEnabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.TimesheetImportToken, Token);
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/timesheet/import/tasks");
        req.Headers.Add(TokenHeader, Token);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/timesheet/import/status")]
    public async Task Admin_config_requires_authentication(string url)
    {
        using var factory = new SecurityBaselineFactory();
        using var client = factory.CreateClient();

        var res = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
