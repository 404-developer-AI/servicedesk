using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.0 — the customer-portal authorization boundary.
///   * RequireCustomer is Customer-only AND an amr whitelist ("pwd+mfa"):
///     pending / password-only customer sessions and every agent/admin
///     session are refused on /api/portal/tickets.
///   * Agent endpoints stay closed to customers (role gate).
///   * The whole portal surface answers 404 while Portal.Enabled is off.
///   * Anonymous portal auth POSTs are CSRF-exempt; session-bound ones are not.
public sealed class PortalAuthorizationTests
{
    [Fact]
    public async Task Pending_customer_session_is_forbidden_on_portal_tickets()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var client = await ClientWithSession(factory, "Customer", SessionAuthenticationHandler.AmrPending);

        var response = await client.GetAsync("/api/portal/tickets/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Password_only_customer_session_is_forbidden_on_portal_tickets()
    {
        // Unlike the agent policies (deny-pending blacklist), the portal
        // whitelists pwd+mfa — TOTP is mandatory for customers.
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var client = await ClientWithSession(factory, "Customer", "pwd");

        var response = await client.GetAsync("/api/portal/tickets/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mfa_complete_customer_session_passes_the_portal_gate()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var client = await ClientWithSession(factory, "Customer", "pwd+mfa");

        var response = await client.GetAsync("/api/portal/tickets/");

        // The fake host has no Postgres, so the handler itself may fail
        // later — what matters is that authorization did not refuse it.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Agent")]
    public async Task Agents_and_admins_are_forbidden_on_portal_tickets(string role)
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var client = await ClientWithSession(factory, role, "pwd+mfa");

        var response = await client.GetAsync("/api/portal/tickets/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Customer_session_is_forbidden_on_agent_endpoints()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientWithSession(factory, "Customer", "pwd+mfa");

        var tickets = await client.GetAsync("/api/tickets/");
        var audit = await client.GetAsync("/api/audit/");
        var totpDisable = await client.PostAsync("/api/auth/2fa/disable", null);

        Assert.Equal(HttpStatusCode.Forbidden, tickets.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        // The agent-side TOTP self-service moved to RequireAgent so a
        // customer can never switch off their mandatory authenticator.
        Assert.Equal(HttpStatusCode.Forbidden, totpDisable.StatusCode);
    }

    [Fact]
    public async Task Portal_public_surface_is_404_while_disabled()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "false");
        var client = factory.CreateClient();

        var config = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/portal/config");
        Assert.NotNull(config);
        Assert.Equal("False", config!["enabled"].ToString());

        var login = await client.PostAsJsonAsync("/api/portal/auth/public/login", new { email = "a@b.co", password = "x" });
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);

        var register = await client.PostAsJsonAsync("/api/portal/auth/public/register",
            new { email = "a@b.co", password = "averylongpassword", displayName = "A B" });
        Assert.Equal(HttpStatusCode.NotFound, register.StatusCode);
    }

    [Fact]
    public async Task Anonymous_portal_login_is_csrf_exempt_but_session_bound_endpoints_are_not()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var anon = factory.CreateClient();

        // No XSRF cookie/header: must not be 403 from the CSRF middleware
        // (the login itself fails on credentials → 401).
        var login = await anon.PostAsJsonAsync("/api/portal/auth/public/login", new { email = "nobody@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // Session-bound portal POST without the XSRF pair → CSRF 403.
        var client = await ClientWithSession(factory, "Customer", SessionAuthenticationHandler.AmrPending);
        var verify = await client.PostAsJsonAsync("/api/portal/auth/2fa/verify", new { code = "123456" });
        Assert.Equal(HttpStatusCode.Forbidden, verify.StatusCode);
    }

    [Fact]
    public async Task Agent_login_refuses_customer_accounts()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();
        // FakeUserService creates Admin rows only, so seed a Customer row
        // through the same helper it exposes and then attempt the agent login.
        var hash = factory.Services.GetService(typeof(Infrastructure.Auth.IPasswordHasher)) as Infrastructure.Auth.IPasswordHasher;
        Assert.NotNull(hash);
        factory.Users.Add("customer@example.com", hash!.Hash("correct-horse-battery-staple"), "Customer");

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "customer@example.com", password = "correct-horse-battery-staple" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> ClientWithSession(SecurityBaselineFactory factory, string role, string amr)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: amr);
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieName}={sessionId}");
        return client;
    }
}
