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

/// v0.1.52 — access boundaries of ticket forms: customers never reach the
/// forms API (staff-only, internal data), agents never reach the admin
/// template API, and the search source yields zero hits for anyone without
/// queue access. Queue access on a specific ticket/form is enforced by the
/// shared mutation precheck inside TicketFormService (404 like the ticket API).
public sealed class TicketFormAccessTests
{
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", $"/api/tickets/{AnyId}/forms")]
    [InlineData("GET", $"/api/tickets/{AnyId}/forms/templates")]
    [InlineData("GET", $"/api/tickets/{AnyId}/forms/templates/{AnyId}")]
    [InlineData("GET", $"/api/tickets/{AnyId}/forms/article-options?scope=all")]
    [InlineData("GET", $"/api/tickets/{AnyId}/forms/contact-options")]
    [InlineData("GET", $"/api/ticket-forms/{AnyId}")]
    [InlineData("GET", $"/api/ticket-forms/{AnyId}/versions")]
    public async Task Customer_cannot_reach_the_forms_api(string method, string url)
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, "Customer");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_save_a_form()
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, "Customer");

        var response = await client.PostAsJsonAsync($"/api/tickets/{AnyId}/forms",
            new { templateId = AnyId, answers = new { } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/settings/form-templates/")]
    [InlineData("GET", $"/api/settings/form-templates/{AnyId}")]
    [InlineData("DELETE", $"/api/settings/form-templates/{AnyId}")]
    public async Task Agent_cannot_manage_templates(string method, string url)
    {
        using var factory = new SecurityBaselineFactory();
        var client = await ClientAsync(factory, "Agent");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- global search ------------------------------------------------------

    [Fact]
    public async Task Search_gives_customer_zero_hits_without_hitting_db()
    {
        var src = new TicketFormSearchSource(null!);
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", null);

        Assert.False(src.IsAvailableFor(customer));
        var result = await src.SearchAsync(new SearchRequest("laptop", null, 10, 0), customer, default);
        Assert.Empty(result.Hits);
        Assert.Equal(SearchSourceKind.TicketForms, result.Kind);
    }

    [Fact]
    public async Task Search_gives_agent_without_queues_zero_hits_without_hitting_db()
    {
        var src = new TicketFormSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());

        var result = await src.SearchAsync(new SearchRequest("laptop", null, 10, 0), agent, default);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Search_is_available_to_staff()
    {
        var src = new TicketFormSearchSource(null!);
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>())));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
    }

    private static async Task<HttpClient> ClientAsync(SecurityBaselineFactory factory, string role)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        }));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }
}
