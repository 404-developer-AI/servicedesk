using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.27 — GET /api/iso/config: the agent-readable projection the ISO
/// classification buttons use instead of the admin-only settings list.
public sealed class IsoConfigEndpointTests
{
    private static readonly Guid BoundQueue = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    [Fact]
    public async Task Mgm_agent_gets_the_bound_queue_id_only()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Iso27001.QueueId, BoundQueue.ToString());
        var client = await ClientAsync(factory, "Agent", new IsoFlags(Mgm: true, Dpo: false));

        var response = await client.GetAsync("/api/iso/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Exactly one property: no other setting may ride along.
        using var doc = global::System.Text.Json.JsonDocument.Parse(body);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "queueId" }, props);
        Assert.Equal(BoundQueue, doc.RootElement.GetProperty("queueId").GetGuid());
    }

    [Fact]
    public async Task Unconfigured_workflow_returns_null_queue()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Iso27001.QueueId, "");
        var client = await ClientAsync(factory, "Agent", new IsoFlags(Mgm: true, Dpo: false));

        var dto = await client.GetFromJsonAsync<Dto>("/api/iso/config");

        Assert.NotNull(dto);
        Assert.Null(dto!.QueueId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Agent_without_mgm_flag_gets_403(bool mgm, bool dpo)
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Iso27001.QueueId, BoundQueue.ToString());
        var client = await ClientAsync(factory, "Agent", new IsoFlags(mgm, dpo));

        var response = await client.GetAsync("/api/iso/config");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(BoundQueue.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Customer_is_refused_even_with_flag()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, "Customer", new IsoFlags(Mgm: true, Dpo: true));

        var response = await client.GetAsync("/api/iso/config");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    private sealed record Dto(Guid? QueueId);

    private static async Task<HttpClient> ClientAsync(SecurityBaselineFactory factory, string role, IsoFlags flags)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        factory.Users.IsoFlagsByUser[userId] = flags;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }
}
