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

/// v0.1.54 — group tags: managing groups is admin-only; the shared tags and
/// the picker are staff-only (customers never reach them). The auth gate
/// answers before any handler runs, so no database is needed.
public sealed class TagGroupAuthorizationTests : IClassFixture<SharedHostFixture>
{
    private readonly SharedHostFixture _hosts;

    public TagGroupAuthorizationTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
    }

    [Theory]
    [InlineData("GET", "/api/settings/tag-groups/")]
    [InlineData("POST", "/api/settings/tag-groups/")]
    [InlineData("PUT", "/api/settings/tag-groups/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/settings/tag-groups/00000000-0000-0000-0000-000000000001")]
    public async Task Agents_cannot_manage_groups(string method, string url)
    {
        var factory = _hosts.Base;
        var client = await ClientAsync(factory, "Agent");
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT") req.Content = JsonContent.Create(new { name = "Back office", memberIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/tag-groups/search?q=back")]
    [InlineData("GET", "/api/group-tags/pending")]
    [InlineData("GET", "/api/group-tags/history")]
    [InlineData("POST", "/api/group-tags/00000000-0000-0000-0000-000000000001/ack")]
    public async Task Customers_never_reach_group_tags(string method, string url)
    {
        var factory = _hosts.Base;
        var client = await ClientAsync(factory, "Customer");

        var status = (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode;

        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"got {status}");
    }

    private async Task<HttpClient> ClientAsync(SecurityBaselineFactory factory, string role)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: role == "Customer" ? "pwd+mfa" : "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var host = _hosts.UnreachableDb;
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }
}
