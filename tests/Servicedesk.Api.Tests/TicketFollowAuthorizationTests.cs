using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.54 — following tickets is staff-only: a customer never reaches the
/// Following list or the follow / unfollow / seen routes.
public sealed class TicketFollowAuthorizationTests : IClassFixture<SharedHostFixture>
{
    private readonly SharedHostFixture _hosts;

    public TicketFollowAuthorizationTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
    }

    private const string T = "/api/tickets/00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", "/api/following")]
    [InlineData("PUT", T + "/follow")]
    [InlineData("DELETE", T + "/follow")]
    [InlineData("POST", T + "/follow/seen")]
    [InlineData("PUT", T + "/follow/note")]
    public async Task Customers_never_reach_following(string method, string url)
    {
        var factory = _hosts.Base;
        var client = await factory.SignedInClientAsync("Customer", _hosts.UnreachableDb);
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT") req.Content = JsonContent.Create(new { bodyHtml = "<p>x</p>" });

        var status = (await client.SendAsync(req)).StatusCode;

        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"got {status}");
    }
}
