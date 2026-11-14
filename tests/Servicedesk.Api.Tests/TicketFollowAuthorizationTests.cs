using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.54 — following tickets is staff-only: a customer never reaches the
/// Following list or the follow / unfollow / seen routes.
public sealed class TicketFollowAuthorizationTests
{
    private const string T = "/api/tickets/00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", "/api/following")]
    [InlineData("PUT", T + "/follow")]
    [InlineData("DELETE", T + "/follow")]
    [InlineData("POST", T + "/follow/seen")]
    public async Task Customers_never_reach_following(string method, string url)
    {
        using var factory = new SecurityBaselineFactory();
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = "Customer";
        var sessionId = await factory.Sessions.CreateAsync(userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd+mfa");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<NpgsqlDataSource>();
            s.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        })).CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT") req.Content = JsonContent.Create(new { bodyHtml = "<p>x</p>" });

        var status = (await client.SendAsync(req)).StatusCode;

        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"got {status}");
    }
}
