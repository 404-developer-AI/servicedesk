using System.Net;
using Microsoft.AspNetCore.Http;
using Servicedesk.Api.Security;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class RateLimitingTests
{
    [Fact]
    public async Task ExceedingGlobalLimit_Returns429_AndAuditsRejection()
    {
        using var factory = new SecurityBaselineFactory()
            .WithConfig("Security:RateLimit:Global:PermitPerWindow", "3")
            .WithConfig("Security:RateLimit:Global:WindowSeconds", "60");

        var client = factory.CreateClient();

        // First 3 pass, 4th rejected.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.GetAsync("/api/auth/config");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var rejected = await client.GetAsync("/api/auth/config");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Give the async OnRejected a moment to flush.
        await Task.Delay(50);
        Assert.Contains(factory.Audit.Events, e => e.EventType == "rate_limited");
    }

    // v0.1.4 — signed-in callers are budgeted per session cookie, so two
    // sessions on one address (an office NAT) no longer drain each other.
    [Fact]
    public async Task TwoSessionsOnOneIp_GetSeparateBudgets()
    {
        using var factory = new SecurityBaselineFactory()
            .WithConfig("Security:RateLimit:Global:PermitPerWindow", "3")
            .WithConfig("Security:RateLimit:Global:WindowSeconds", "60")
            .WithConfig("Security:RateLimit:Global:IpCeilingMultiplier", "5");

        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsSession(client, "session-a")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsSession(client, "session-a")).StatusCode);

        // A second session on the same IP still has its own full budget.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsSession(client, "session-b")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsSession(client, "session-b")).StatusCode);

        // And so does the anonymous (no-cookie) caller on that IP.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/config")).StatusCode);
    }

    // The per-IP ceiling is the abuse bound: forging fresh cookies must not
    // buy unlimited budget from one address.
    [Fact]
    public async Task IpCeiling_CapsAllSessionsFromOneAddress()
    {
        using var factory = new SecurityBaselineFactory()
            .WithConfig("Security:RateLimit:Global:PermitPerWindow", "2")
            .WithConfig("Security:RateLimit:Global:WindowSeconds", "60")
            .WithConfig("Security:RateLimit:Global:IpCeilingMultiplier", "2"); // ceiling = 4

        var client = factory.CreateClient();

        // Four distinct "sessions", one request each: 4 pass (ceiling), the
        // 5th is rejected even though its own session budget is untouched.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsSession(client, $"forged-{i}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsSession(client, "forged-99")).StatusCode);
    }

    // Connection plumbing (hub negotiate, version probe, maintenance banner)
    // must survive an exhausted per-session budget so a refresh can still
    // reconnect — but it stays inside the per-IP ceiling.
    [Fact]
    public async Task InfraCalls_BypassSessionBudget_ButNotIpCeiling()
    {
        using var factory = new SecurityBaselineFactory()
            .WithConfig("Security:RateLimit:Global:PermitPerWindow", "2")
            .WithConfig("Security:RateLimit:Global:WindowSeconds", "60")
            .WithConfig("Security:RateLimit:Global:IpCeilingMultiplier", "3"); // ceiling = 6

        var client = factory.CreateClient();

        // Exhaust the session budget (2 permits).
        Assert.Equal(HttpStatusCode.OK, (await GetAsSession(client, "s")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsSession(client, "s")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsSession(client, "s")).StatusCode);

        // Version probes still answer on that exhausted session — until the
        // IP ceiling (6) is used up. The session-rejected call above did not
        // touch the ceiling (session link runs first), so 2 counted so far:
        // four probes pass, the fifth is rejected.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsSession(client, "s", "/api/system/version")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsSession(client, "s", "/api/system/version")).StatusCode);
    }

    [Fact]
    public async Task SystemPolls_AreNeverThrottled()
    {
        using var factory = new SecurityBaselineFactory()
            .WithConfig("Security:RateLimit:Global:PermitPerWindow", "1")
            .WithConfig("Security:RateLimit:Global:WindowSeconds", "60")
            .WithConfig("Security:RateLimit:Global:IpCeilingMultiplier", "1");

        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var res = await client.GetAsync("/api/system/time");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, res.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> GetAsSession(HttpClient client, string cookieValue, string path = "/api/auth/config")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"sd_session={cookieValue}");
        return client.SendAsync(req);
    }
}

public sealed class GlobalRateLimitPartitionerTests
{
    [Fact]
    public void SessionCookie_KeysPerSession_AndNeverExposesRawToken()
    {
        var a = Context(cookie: "sd_session=tokenA");
        var b = Context(cookie: "sd_session=tokenB");
        var a2 = Context(cookie: "sd_session=tokenA");

        var keyA = GlobalRateLimitPartitioner.SessionOrIpKey(a, "sd_session", "sd_portal");
        var keyB = GlobalRateLimitPartitioner.SessionOrIpKey(b, "sd_session", "sd_portal");
        var keyA2 = GlobalRateLimitPartitioner.SessionOrIpKey(a2, "sd_session", "sd_portal");

        Assert.StartsWith("s:", keyA);
        Assert.Equal(keyA, keyA2);
        Assert.NotEqual(keyA, keyB);
        Assert.DoesNotContain("tokenA", keyA);
    }

    [Fact]
    public void PortalCookie_UsedWhenNoStaffCookie_StaffWinsWhenBoth()
    {
        var portalOnly = Context(cookie: "sd_portal=p1");
        var both = Context(cookie: "sd_portal=p1; sd_session=s1");
        var staffOnly = Context(cookie: "sd_session=s1");

        Assert.StartsWith("p:", GlobalRateLimitPartitioner.SessionOrIpKey(portalOnly, "sd_session", "sd_portal"));
        Assert.Equal(
            GlobalRateLimitPartitioner.SessionOrIpKey(staffOnly, "sd_session", "sd_portal"),
            GlobalRateLimitPartitioner.SessionOrIpKey(both, "sd_session", "sd_portal"));
    }

    [Fact]
    public void NoCookie_FallsBackToIp()
    {
        var ctx = Context(cookie: null, ip: "203.0.113.7");
        Assert.Equal("ip:203.0.113.7", GlobalRateLimitPartitioner.SessionOrIpKey(ctx, "sd_session", "sd_portal"));
    }

    [Theory]
    [InlineData("GET", "/hubs/presence/negotiate", true)]
    [InlineData("POST", "/hubs/notifications/negotiate", true)]
    [InlineData("GET", "/api/system/version", true)]
    [InlineData("GET", "/api/system/maintenance", true)]
    [InlineData("PUT", "/api/system/maintenance", false)]
    [InlineData("GET", "/api/tickets", false)]
    [InlineData("GET", "/hubs/presence", false)]
    public void Infra_Detection(string method, string path, bool expected)
    {
        var ctx = Context(cookie: null, method: method, path: path);
        Assert.Equal(expected, GlobalRateLimitPartitioner.IsInfra(ctx));
    }

    [Theory]
    [InlineData("GET", "/api/tickets/abc/attachments/def", true)]
    [InlineData("GET", "/api/tickets/abc/mail/m/attachments/x", true)]
    [InlineData("POST", "/api/tickets/abc/attachments", false)]
    [InlineData("GET", "/api/v1/ticket_attachment/1/2/3", false)]
    public void AttachmentGet_Detection(string method, string path, bool expected)
    {
        var ctx = Context(cookie: null, method: method, path: path);
        Assert.Equal(expected, GlobalRateLimitPartitioner.IsAttachmentGet(ctx));
    }

    private static DefaultHttpContext Context(string? cookie, string ip = "198.51.100.1", string method = "GET", string path = "/api/tickets")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (cookie is not null) ctx.Request.Headers.Cookie = cookie;
        return ctx;
    }
}
