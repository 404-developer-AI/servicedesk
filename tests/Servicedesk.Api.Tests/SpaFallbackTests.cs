using System.Net;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SpaFallbackTests : IClassFixture<SecurityBaselineFactory>
{
    private readonly SecurityBaselineFactory _factory;

    public SpaFallbackTests(SecurityBaselineFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnknownApiRoute_ReturnsJson404_NotHtmlFallback()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/this/definitely/does/not/exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A leaked HTML fallback on /api/* would break JSON clients — the regex
        // exclusion in MapFallbackToFile must keep this a clean JSON 404.
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.NotEqual("text/html", contentType);
    }

    [Fact]
    public async Task UnknownHubRoute_DoesNotHitFallback()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/hubs/nope");

        // Either 404 or 400, but never an HTML-fallback document.
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.NotEqual("text/html", contentType);
    }
}
