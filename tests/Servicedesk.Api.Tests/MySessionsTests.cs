using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Mobile web — "My sessions" (sign out a lost or shared device from another
/// one), the no-store default on API responses and the cache clear on
/// sign-out. The session id is the cookie token, so these tests also pin that
/// it never appears in a response.
public sealed class MySessionsTests
{
    [Fact]
    public async Task List_returns_only_own_open_sessions_marks_current_and_never_exposes_the_token()
    {
        using var factory = new SecurityBaselineFactory();
        var userId = Guid.NewGuid();
        var (client, currentSessionId) = await AgentClient(factory, userId);
        var otherDevice = await factory.Sessions.CreateAsync(userId, null, null, TimeSpan.FromHours(1), "pwd+mfa");
        var foreign = await factory.Sessions.CreateAsync(Guid.NewGuid(), null, null, TimeSpan.FromHours(1), "pwd+mfa");

        var response = await client.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(currentSessionId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherDevice.ToString(), raw, StringComparison.OrdinalIgnoreCase);

        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var publicIds = items.Select(i => i.GetProperty("publicId").GetGuid()).ToHashSet();
        Assert.Contains(factory.Sessions.PublicIdOf(currentSessionId), publicIds);
        Assert.Contains(factory.Sessions.PublicIdOf(otherDevice), publicIds);
        Assert.DoesNotContain(factory.Sessions.PublicIdOf(foreign), publicIds);
        var current = items.Single(i => i.GetProperty("current").GetBoolean());
        Assert.Equal(factory.Sessions.PublicIdOf(currentSessionId), current.GetProperty("publicId").GetGuid());
    }

    [Fact]
    public async Task Revoking_another_device_kills_that_session_and_keeps_the_current_one()
    {
        using var factory = new SecurityBaselineFactory();
        var userId = Guid.NewGuid();
        var (client, currentSessionId) = await AgentClient(factory, userId);
        var lostPhone = await factory.Sessions.CreateAsync(userId, null, null, TimeSpan.FromHours(1), "pwd+mfa");

        var response = await client.DeleteAsync($"/api/auth/sessions/{factory.Sessions.PublicIdOf(lostPhone)}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(factory.Sessions.IsRevoked(lostPhone));
        Assert.False(factory.Sessions.IsRevoked(currentSessionId));
        Assert.Contains(factory.Audit.Events, e => e.EventType == "session_revoked");

        var probe = await CookieClient(factory, lostPhone).GetAsync("/api/settings/navigation");
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
    }

    [Fact]
    public async Task Another_users_session_cannot_be_revoked()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, _) = await AgentClient(factory, Guid.NewGuid());
        var foreign = await factory.Sessions.CreateAsync(Guid.NewGuid(), null, null, TimeSpan.FromHours(1), "pwd+mfa");

        var response = await client.DeleteAsync($"/api/auth/sessions/{factory.Sessions.PublicIdOf(foreign)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(factory.Sessions.IsRevoked(foreign));
    }

    [Fact]
    public async Task The_current_session_is_not_revocable_through_the_sessions_endpoint()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, currentSessionId) = await AgentClient(factory, Guid.NewGuid());

        var response = await client.DeleteAsync($"/api/auth/sessions/{factory.Sessions.PublicIdOf(currentSessionId)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(factory.Sessions.IsRevoked(currentSessionId));
    }

    [Fact]
    public async Task Revoke_others_signs_out_every_other_device_only()
    {
        using var factory = new SecurityBaselineFactory();
        var userId = Guid.NewGuid();
        var (client, currentSessionId) = await AgentClient(factory, userId);
        var tablet = await factory.Sessions.CreateAsync(userId, null, null, TimeSpan.FromHours(1), "pwd+mfa");
        var phone = await factory.Sessions.CreateAsync(userId, null, null, TimeSpan.FromHours(1), "pwd+mfa");
        var foreign = await factory.Sessions.CreateAsync(Guid.NewGuid(), null, null, TimeSpan.FromHours(1), "pwd+mfa");

        var response = await client.PostAsJsonAsync("/api/auth/sessions/revoke-others", new { });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(factory.Sessions.IsRevoked(tablet));
        Assert.True(factory.Sessions.IsRevoked(phone));
        Assert.False(factory.Sessions.IsRevoked(currentSessionId));
        Assert.False(factory.Sessions.IsRevoked(foreign));
    }

    [Fact]
    public async Task Sessions_endpoints_require_a_signed_in_agent()
    {
        using var factory = new SecurityBaselineFactory();
        var anonymous = factory.CreateClient();

        var list = await anonymous.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    [Fact]
    public async Task Api_responses_default_to_no_store()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, _) = await AgentClient(factory, Guid.NewGuid());

        var response = await client.GetAsync("/api/settings/navigation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Sign_out_asks_the_browser_to_drop_its_cache()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, _) = await AgentClient(factory, Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/api/auth/logout", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Clear-Site-Data", out var values));
        Assert.Equal("\"cache\"", Assert.Single(values));
    }

    [Fact]
    public async Task Mobile_settings_projection_is_agent_readable_and_anonymous_is_refused()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, _) = await AgentClient(factory, Guid.NewGuid());

        var ok = await client.GetFromJsonAsync<JsonElement>("/api/settings/mobile");
        Assert.True(ok.GetProperty("autoRedirect").GetBoolean());

        var anonymous = await factory.CreateClient().GetAsync("/api/settings/mobile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- plumbing ----------------------------------------------------------

    private static async Task<(HttpClient Client, Guid SessionId)> AgentClient(SecurityBaselineFactory factory, Guid userId)
    {
        factory.Sessions.Roles[userId] = "Agent";
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd+mfa");
        return (CookieClient(factory, sessionId), sessionId);
    }

    private static HttpClient CookieClient(SecurityBaselineFactory factory, Guid sessionId)
    {
        var cookieName = factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName).GetAwaiter().GetResult();
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }
}
