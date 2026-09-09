using System.Net;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SessionTouchTests
{
    // v0.1.5 — an authenticated request must slide the idle window. The fix
    // touches last_seen_utc on every cache-miss validation. Before it, the
    // touch sat behind a throttle whose interval equalled the cache lifetime,
    // so it never elapsed (and it used the request's own cancellation token,
    // which is cancelled the moment the response is sent) — last_seen_utc
    // never moved off the login time and every session hard-expired at the
    // idle timeout regardless of activity (the hourly forced re-logins).
    [Fact]
    public async Task Authenticated_request_slides_last_seen()
    {
        using var factory = new SecurityBaselineFactory();
        var sessionId = await factory.Sessions.CreateAsync(
            Guid.NewGuid(), ip: null, userAgent: null, lifetime: TimeSpan.FromHours(12), amr: "pwd+mfa");

        // An agent who has been working a while: last active 30 min ago, still
        // inside the 60-min idle window.
        factory.Sessions.Backdate(sessionId, TimeSpan.FromMinutes(30));
        var before = factory.Sessions.LastSeen(sessionId)!.Value;

        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieName}={sessionId}");

        var res = await client.GetAsync("/api/audit/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The single authenticated request must have refreshed last_seen to ~now.
        var after = factory.Sessions.LastSeen(sessionId)!.Value;
        Assert.True(
            after > before.AddMinutes(20),
            $"last_seen should have jumped to ~now on an authenticated request (was {before:o}, now {after:o})");
    }
}
