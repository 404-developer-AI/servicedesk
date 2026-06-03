using System.Net;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.57 — server-side 2FA enforcement. A session that cleared the password
/// step but still owes its TOTP challenge (amr "mfa-pending") must NOT reach a
/// role-gated endpoint; only the /2fa/verify upgrade to "pwd+mfa" unlocks it.
/// This guards against the MFA-bypass where the password-only cookie was usable
/// directly via curl/proxy. The FakeSessionService synthesises an Admin user,
/// so reaching the Admin-only audit endpoint isolates the amr gate.
public sealed class MfaEnforcementTests
{
    [Fact]
    public async Task Pending_mfa_session_is_forbidden_on_protected_endpoint()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientWithSession(factory, SessionAuthenticationHandler.AmrPending);

        var response = await client.GetAsync("/api/audit/");

        // Authenticated (valid cookie) but the MFA gate denies → 403, never 200.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Verified_mfa_session_passes_the_gate()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientWithSession(factory, "pwd+mfa");

        var response = await client.GetAsync("/api/audit/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Password_only_session_passes_the_gate()
    {
        // A user without 2FA gets a fully-authorized "pwd" session — the gate
        // must not block them (it's a deny-pending blacklist, not a whitelist).
        using var factory = new SecurityBaselineFactory();
        var client = await ClientWithSession(factory, "pwd");

        var response = await client.GetAsync("/api/audit/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpClient> ClientWithSession(SecurityBaselineFactory factory, string amr)
    {
        var sessionId = await factory.Sessions.CreateAsync(
            Guid.NewGuid(), ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: amr);
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieName}={sessionId}");
        return client;
    }
}
