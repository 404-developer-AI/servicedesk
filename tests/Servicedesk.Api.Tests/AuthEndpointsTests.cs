using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class AuthEndpointsTests
{
    [Fact]
    public async Task SetupStatus_Reports_Available_When_No_Users_Exist()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/setup/status");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task SetupStatus_Reports_Unavailable_After_First_Admin_Is_Created()
    {
        using var factory = new SecurityBaselineFactory();
        await factory.Users.CreateFirstAdminAsync("admin@example.com", "$argon2id$fake");

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/auth/setup/status");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Me_Returns_Null_User_Without_Session_Cookie()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, json.GetProperty("user").ValueKind);
        Assert.NotNull(json.GetProperty("serverTimeUtc").GetString());
    }

    [Fact]
    public async Task Audit_Endpoint_Returns_401_Without_Session()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/audit/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Audit_Endpoint_Ignores_Legacy_DevRole_Header()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Role", "Admin");

        var response = await client.GetAsync("/api/audit/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
