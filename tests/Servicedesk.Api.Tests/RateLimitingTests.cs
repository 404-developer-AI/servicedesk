using System.Net;
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
            var ok = await client.GetAsync("/api/system/version");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var rejected = await client.GetAsync("/api/system/version");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Give the async OnRejected a moment to flush.
        await Task.Delay(50);
        Assert.Contains(factory.Audit.Events, e => e.EventType == "rate_limited");
    }
}
