using System.Net;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.12 stap 3 — verifies the agent-typeahead endpoint used by the
/// @@-mention popover enforces the RequireAgent policy *before* the handler
/// (and its database-bound <see cref="Infrastructure.Auth.IUserService"/>)
/// runs. Unauthenticated callers must be rejected with 401 so a customer
/// can never enumerate agents via the typeahead route.
public sealed class MentionEndpointTests : IClassFixture<SecurityBaselineFactory>
{
    private readonly SecurityBaselineFactory _factory;

    public MentionEndpointTests(SecurityBaselineFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/users/agents/search")]
    [InlineData("/api/users/agents/search?q=alice")]
    [InlineData("/api/users/agents/search?q=bob&limit=10")]
    public async Task Agent_typeahead_rejects_unauthenticated(string url)
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
