using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.54 — replies and likes on internal notes are staff-only: a customer
/// never reaches the routes, and the reply search source gives customers and
/// queue-less agents zero hits without touching the database.
public sealed class NoteThreadAuthorizationTests
{
    private const string T = "/api/tickets/00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", T + "/note-threads")]
    [InlineData("POST", T + "/events/1/replies")]
    [InlineData("PUT", T + "/replies/1")]
    [InlineData("DELETE", T + "/replies/1")]
    [InlineData("PUT", T + "/events/1/like")]
    [InlineData("DELETE", T + "/events/1/like")]
    [InlineData("PUT", T + "/replies/1/like")]
    [InlineData("DELETE", T + "/replies/1/like")]
    public async Task Customers_never_reach_note_threads(string method, string url)
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

    [Fact]
    public async Task Reply_search_gives_customers_and_queue_less_agents_zero_hits()
    {
        // null! data source: an unavailable principal must never reach the DB.
        var src = new NoteReplySearchSource(null!);
        var request = new SearchRequest("licence", null, 20, 0);

        Assert.Empty((await src.SearchAsync(request, new SearchPrincipal(Guid.NewGuid(), "Customer", new[] { Guid.NewGuid() }), default)).Hits);
        Assert.Empty((await src.SearchAsync(request, new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>()), default)).Hits);
    }
}
