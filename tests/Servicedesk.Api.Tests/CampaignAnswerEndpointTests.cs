using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.67 (phase 2) — the public campaign answer endpoints need no session
/// and no CSRF token (the customer has neither), and a malformed token is a
/// plain 404 before any database work (the test host's database is
/// unreachable, so reaching it would surface as a 500).
public sealed class CampaignAnswerEndpointTests : IClassFixture<SharedHostFixture>
{
    private readonly SharedHostFixture _hosts;

    public CampaignAnswerEndpointTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task A_malformed_token_is_a_404_for_an_anonymous_caller(string method)
    {
        var client = _hosts.UnreachableDb.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), "/api/campaign-answers/not-a-real-token");
        if (method == "POST") request.Content = JsonContent.Create(new { choice = "Approved", note = "fine" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
