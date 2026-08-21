using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.3 — behaviour added by the v0.1.1 security audit:
/// #1 Security.TwoFactor.Required forces enrollment at sign-in,
/// #7 enroll-begin refuses an already-enrolled account and disable demands a
///    valid code, and
/// #8 the self-service password change verifies the current password and
///    revokes every other session.
public sealed class AuthHardeningV013Tests
{
    // ---- #1 forced enrollment ---------------------------------------------

    [Fact]
    public async Task Required_flag_without_totp_mints_pending_session_and_flags_enrollment()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Security.TwoFactorRequired, "true");
        factory.Totp.Enabled = false;
        var client = factory.CreateClient();
        SeedLocalAgent(factory, "agent@example.com", "correct horse battery staple");

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "agent@example.com", password = "correct horse battery staple" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enrollmentRequired").GetBoolean());
        Assert.False(body.GetProperty("twoFactorRequired").GetBoolean());

        // The minted session is pending: role-gated endpoints refuse it…
        var cookies = ExtractCookies(login);
        var gated = await SendWithCookies(client, HttpMethod.Get, "/api/settings/navigation", cookies, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);

        // …but the enrollment endpoints accept it.
        var begin = await SendWithCookies(client, HttpMethod.Post, "/api/auth/2fa/enroll/begin", cookies, csrf: true);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
    }

    [Fact]
    public async Task Required_flag_off_keeps_password_only_login_fully_authorized()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Totp.Enabled = false;
        var client = factory.CreateClient();
        SeedLocalAgent(factory, "agent@example.com", "correct horse battery staple");

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "agent@example.com", password = "correct horse battery staple" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enrollmentRequired").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_caller_cannot_reach_enroll_begin()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var begin = await client.PostAsJsonAsync("/api/auth/2fa/enroll/begin", new { });

        // The endpoint deliberately carries no policy (pending sessions must
        // pass), so the refusal for a cold caller comes from the CSRF
        // middleware (403); with a CSRF pair but no session the handler's
        // own principal check answers 401. Either way: no anonymous access.
        Assert.Equal(HttpStatusCode.Forbidden, begin.StatusCode);

        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/2fa/enroll/begin")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Cookie", $"{DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        request.Headers.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        var withCsrf = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, withCsrf.StatusCode);
    }

    // ---- #7 enrollment guard + step-up disable ----------------------------

    [Fact]
    public async Task Enroll_begin_conflicts_when_already_enrolled()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Totp.Enabled = true;
        var client = await AgentClient(factory);

        var begin = await client.PostAsJsonAsync("/api/auth/2fa/enroll/begin", new { });

        Assert.Equal(HttpStatusCode.Conflict, begin.StatusCode);
    }

    [Fact]
    public async Task Disable_with_wrong_code_is_refused_and_totp_stays_on()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Totp.Enabled = true;
        factory.Totp.VerifyResult = TwoFactorResult.Rejected;
        var userId = Guid.NewGuid();
        var client = await AgentClient(factory, userId);
        SeedUser(factory, userId, "agent@example.com");

        var disable = await client.PostAsJsonAsync("/api/auth/2fa/disable", new { code = "000000" });

        Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
        Assert.True(factory.Totp.Enabled);
    }

    [Fact]
    public async Task Disable_with_valid_code_succeeds()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Totp.Enabled = true;
        factory.Totp.VerifyResult = TwoFactorResult.TotpAccepted;
        var userId = Guid.NewGuid();
        var client = await AgentClient(factory, userId);
        SeedUser(factory, userId, "agent@example.com");

        var disable = await client.PostAsJsonAsync("/api/auth/2fa/disable", new { code = "123456" });

        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False(factory.Totp.Enabled);
    }

    // ---- #8 password change -----------------------------------------------

    [Fact]
    public async Task Change_password_refuses_wrong_current_password()
    {
        using var factory = new SecurityBaselineFactory();
        var user = SeedLocalAgent(factory, "agent@example.com", "old password 12345");
        var client = await AgentClient(factory, user.Id);

        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "wrong", newPassword = "brand new password 42" });

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact]
    public async Task Change_password_succeeds_and_revokes_other_sessions()
    {
        using var factory = new SecurityBaselineFactory();
        var user = SeedLocalAgent(factory, "agent@example.com", "old password 12345");
        var client = await AgentClient(factory, user.Id);

        // A second live session for the same user (the "stolen cookie").
        var otherSessionId = await factory.Sessions.CreateAsync(
            user.Id, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");

        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "old password 12345", newPassword = "brand new password 42" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // The other session is dead now; the changing session survives.
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var otherClient = factory.CreateClient();
        otherClient.DefaultRequestHeaders.Add("Cookie", $"{cookieName}={otherSessionId}");
        var probe = await otherClient.GetAsync("/api/settings/navigation");
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
    }

    // ---- plumbing ----------------------------------------------------------

    private static ApplicationUser SeedLocalAgent(SecurityBaselineFactory factory, string email, string password)
    {
        var hasher = factory.Services.GetRequiredService<IPasswordHasher>();
        var user = factory.Users.Add(email, hasher.Hash(password), "Agent");
        factory.Sessions.Roles[user.Id] = "Agent";
        return user;
    }

    private static void SeedUser(SecurityBaselineFactory factory, Guid userId, string email)
    {
        // The 2FA endpoints re-read the user row for the lockout check, so
        // the session's user id must exist in the fake user store.
        factory.Users.AddWithId(userId, email, "not-a-hash", "Agent");
    }

    private static async Task<HttpClient> AgentClient(SecurityBaselineFactory factory, Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        factory.Sessions.Roles[id] = "Agent";
        var sessionId = await factory.Sessions.CreateAsync(
            id, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd+mfa");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }

    private static Dictionary<string, string> ExtractCookies(HttpResponseMessage response)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var raw in setCookies)
            {
                var pair = raw.Split(';', 2)[0];
                var idx = pair.IndexOf('=');
                // Set-Cookie percent-encodes the value; undo it so the CSRF
                // header (compared against the decoded cookie) matches.
                if (idx > 0) cookies[pair[..idx]] = Uri.UnescapeDataString(pair[(idx + 1)..]);
            }
        }
        return cookies;
    }

    private static Task<HttpResponseMessage> SendWithCookies(
        HttpClient client, HttpMethod method, string url, Dictionary<string, string> cookies, bool csrf)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
        if (csrf && cookies.TryGetValue(DoubleSubmitCsrfMiddleware.CookieName, out var token))
        {
            request.Headers.Add(DoubleSubmitCsrfMiddleware.HeaderName, token);
        }
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { });
        }
        return client.SendAsync(request);
    }
}
