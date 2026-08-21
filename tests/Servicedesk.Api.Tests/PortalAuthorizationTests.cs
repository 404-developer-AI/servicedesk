using System.Linq;
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
    // v0.1.1 fix — a live staff session whose CSRF cookie was wiped (the
    // pre-split shared-cookie bug did exactly that on every portal logout)
    // must get a fresh one from /me, otherwise sign-out itself 403s forever.
    [Fact]
    public async Task Staff_me_remints_a_missing_csrf_cookie()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientWithSession(factory, "Admin", "pwd+mfa"); // no CSRF cookie sent

        var response = await client.GetAsync("/api/auth/me");

        response.EnsureSuccessStatusCode();
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : new List<string>();
        Assert.Contains(setCookies, c =>
            c.StartsWith($"{DoubleSubmitCsrfMiddleware.CookieName}=", StringComparison.Ordinal)
            && !c.StartsWith($"{DoubleSubmitCsrfMiddleware.PortalCookieName}=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Staff_me_leaves_an_existing_csrf_cookie_alone()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientWithSession(factory, "Admin", "pwd+mfa", withCsrf: true);

        var response = await client.GetAsync("/api/auth/me");

        response.EnsureSuccessStatusCode();
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : new List<string>();
        Assert.DoesNotContain(setCookies, c => c.StartsWith($"{DoubleSubmitCsrfMiddleware.CookieName}=", StringComparison.Ordinal));
    }

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

    // ---- v0.1.1 shadow login ----------------------------------------------

    [Fact]
    public async Task Impersonated_session_passes_the_portal_read_gate()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var client = await ClientWithSession(factory, "Customer", SessionAuthenticationHandler.AmrImpersonated);

        var response = await client.GetAsync("/api/portal/tickets/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Impersonated_session_is_refused_on_every_portal_write()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var client = await ClientWithSession(factory, "Customer", SessionAuthenticationHandler.AmrImpersonated, withCsrf: true);

        var create = await client.PostAsJsonAsync("/api/portal/tickets/", new { subject = "x", bodyHtml = "<p>x</p>" });
        var reply = await client.PostAsJsonAsync($"/api/portal/tickets/{Guid.NewGuid()}/messages", new { bodyHtml = "<p>x</p>" });
        var enroll = await client.PostAsync("/api/portal/auth/2fa/enroll/begin", null);
        var verify = await client.PostAsJsonAsync("/api/portal/auth/2fa/verify", new { code = "123456" });

        foreach (var response in new[] { create, reply, enroll, verify })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("impersonated_read_only", body);
        }
    }

    [Fact]
    public async Task Impersonate_endpoint_is_admin_only()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var agent = await ClientWithSession(factory, "Agent", "pwd+mfa", withCsrf: true);
        var customer = await ClientWithSession(factory, "Customer", "pwd+mfa", withCsrf: true);

        var viaAgent = await agent.PostAsync($"/api/portal/admin/accounts/{Guid.NewGuid()}/impersonate", null);
        var viaCustomer = await customer.PostAsync($"/api/portal/admin/accounts/{Guid.NewGuid()}/impersonate", null);

        Assert.Equal(HttpStatusCode.Forbidden, viaAgent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viaCustomer.StatusCode);
    }

    [Fact]
    public async Task Impersonate_refuses_unknown_accounts()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var admin = await ClientWithSession(factory, "Admin", "pwd+mfa", withCsrf: true);

        var response = await admin.PostAsync($"/api/portal/admin/accounts/{Guid.NewGuid()}/impersonate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Impersonate_mints_a_read_only_session_on_the_portal_cookie()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");

        // Seed an Active customer account behind the fake repositories.
        var customer = factory.Users.Add("shadow-me@example.com", "hash", "Customer");
        var repo = (FakePortalAccountRepository)factory.Services.GetService(typeof(Servicedesk.Infrastructure.Portal.IPortalAccountRepository))!;
        repo.Account = new Servicedesk.Infrastructure.Portal.PortalAccountRow(
            customer.Id, customer.Email, Servicedesk.Infrastructure.Portal.PortalAccountStatus.Active, "Shadow Me",
            Servicedesk.Infrastructure.Portal.PortalAccountOrigin.Invitation, true,
            Guid.NewGuid(), "Shadow", "Me", null, null, null, null, DateTime.UtcNow, null, null, null, null, null,
            null, null, null, null, null, true, null, DateTime.UtcNow, DateTime.UtcNow);

        var admin = await ClientWithSession(factory, "Admin", "pwd+mfa", withCsrf: true);
        var response = await admin.PostAsync($"/api/portal/admin/accounts/{customer.Id}/impersonate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var portalCookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.PortalSessionCookieName);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        var portalCookie = setCookies.FirstOrDefault(c => c.StartsWith($"{portalCookieName}=", StringComparison.Ordinal));
        Assert.NotNull(portalCookie);

        // The minted session is the customer's, amr "impersonated", with the
        // admin recorded as impersonator.
        var sessionId = Guid.Parse(portalCookie!.Split(';')[0].Split('=')[1]);
        var validation = await factory.Sessions.ValidateAsync(sessionId, TimeSpan.FromMinutes(60));
        Assert.NotNull(validation);
        Assert.Equal(SessionAuthenticationHandler.AmrImpersonated, validation!.Amr);
        Assert.Equal(customer.Id, validation.User.Id);
        Assert.NotNull(validation.ImpersonatorUserId);
    }

    [Fact]
    public async Task Staff_cookie_does_not_authenticate_the_portal_surface()
    {
        // v0.1.1 — the portal rides its own session cookie. A fully valid
        // customer session presented under the STAFF cookie name must not
        // authenticate on /api/portal/*.
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = "Customer";
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd+mfa");
        var staffCookie = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{staffCookie}={sessionId}");

        var response = await client.GetAsync("/api/portal/tickets/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> ClientWithSession(SecurityBaselineFactory factory, string role, string amr, bool withCsrf = false)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: amr);
        // v0.1.1 — the portal API reads its own cookie; send the session under
        // both names so one helper serves staff and portal endpoints alike.
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var portalCookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.PortalSessionCookieName);
        var client = factory.CreateClient();
        var cookie = $"{cookieName}={sessionId}; {portalCookieName}={sessionId}";
        if (withCsrf)
        {
            // The CSRF cookie is split per realm too (staff vs portal), so send
            // the same token under both names — one helper, both surfaces.
            cookie += $"; {DoubleSubmitCsrfMiddleware.CookieName}=test-csrf-token"
                + $"; {DoubleSubmitCsrfMiddleware.PortalCookieName}=test-csrf-token";
            client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, "test-csrf-token");
        }
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }
}
