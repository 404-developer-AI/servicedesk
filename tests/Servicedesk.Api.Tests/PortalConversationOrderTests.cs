using System.Net;
using System.Net.Http.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.15 — the customer's conversation order on a portal ticket: kept on
/// the account, admin default as fallback, never writable from a shadow
/// session, closed to agents, 404 while the portal is off.
public sealed class PortalConversationOrderTests
{
    private const string Url = "/api/portal/preferences/conversation-order";

    private sealed record Pref(string Order, string Source);

    [Fact]
    public async Task Without_a_choice_the_admin_default_applies()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        factory.Settings.Set(SettingKeys.Portal.ConversationOrder, "newest");
        var (client, _) = await CustomerClient(factory, "pwd+mfa");

        var pref = await client.GetFromJsonAsync<Pref>(Url);

        Assert.Equal(new Pref("newest", "default"), pref);
    }

    [Fact]
    public async Task A_saved_choice_is_returned_and_kept_on_the_account()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var (client, userId) = await CustomerClient(factory, "pwd+mfa", withCsrf: true);

        var put = await client.PutAsJsonAsync(Url, new { order = "Newest" });
        put.EnsureSuccessStatusCode();
        var pref = await client.GetFromJsonAsync<Pref>(Url);

        Assert.Equal(new Pref("newest", "user"), pref);
        Assert.Equal("newest", Repo(factory).ConversationOrders[userId]);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unknown_order_is_rejected(string? order)
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var (client, userId) = await CustomerClient(factory, "pwd+mfa", withCsrf: true);

        var put = await client.PutAsJsonAsync(Url, new { order });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.False(Repo(factory).ConversationOrders.ContainsKey(userId));
    }

    [Fact]
    public async Task A_shadow_session_reads_the_choice_but_never_stores_one()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var (client, userId) = await CustomerClient(factory, SessionAuthenticationHandler.AmrImpersonated, withCsrf: true);
        Repo(factory).ConversationOrders[userId] = "newest";

        var pref = await client.GetFromJsonAsync<Pref>(Url);
        var put = await client.PutAsJsonAsync(Url, new { order = "oldest" });

        Assert.Equal(new Pref("newest", "user"), pref);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Contains("impersonated_read_only", await put.Content.ReadAsStringAsync());
        Assert.Equal("newest", Repo(factory).ConversationOrders[userId]);
    }

    [Fact]
    public void The_preference_rides_the_portal_csrf_cookie()
    {
        Assert.Equal(DoubleSubmitCsrfMiddleware.PortalCookieName, DoubleSubmitCsrfMiddleware.CookieNameFor(Url));
    }

    [Fact]
    public async Task A_stale_stored_value_falls_back_to_the_default()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var (client, userId) = await CustomerClient(factory, "pwd+mfa");
        Repo(factory).ConversationOrders[userId] = "garbage";

        var pref = await client.GetFromJsonAsync<Pref>(Url);

        Assert.Equal(new Pref("oldest", "default"), pref);
    }

    [Fact]
    public async Task The_preference_is_404_while_the_portal_is_off()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "false");
        var (client, _) = await CustomerClient(factory, "pwd+mfa");

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Admin")]
    public async Task Staff_cannot_use_the_customer_preference(string role)
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Portal.Enabled, "true");
        var (client, _) = await Client(factory, role, "pwd+mfa", withCsrf: true, seedViewer: false);

        var get = await client.GetAsync(Url);
        var put = await client.PutAsJsonAsync(Url, new { order = "newest" });

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Theory]
    [InlineData("oldest", true)]
    [InlineData("newest", true)]
    [InlineData("Newest", false)]
    [InlineData("", false)]
    [InlineData("latest", false)]
    public void Default_order_setting_is_validated(string value, bool ok)
    {
        var def = SettingDefaults.All.Single(d => d.Key == SettingKeys.Portal.ConversationOrder);
        Assert.Equal("oldest", def.Value);
        Assert.Equal(ok, SettingValueValidator.Validate(def, value) is null);
    }

    [Theory]
    [InlineData("newest", "oldest", "newest")]
    [InlineData(null, "newest", "newest")]
    [InlineData("bogus", "bogus", "oldest")]
    [InlineData(" OLDEST ", "newest", "oldest")]
    public void Resolve_prefers_the_customer_then_the_default(string? user, string? admin, string expected)
    {
        Assert.Equal(expected, PortalConversationOrder.Resolve(user, admin));
    }

    private static FakePortalAccountRepository Repo(SecurityBaselineFactory factory) =>
        (FakePortalAccountRepository)factory.Services.GetService(typeof(IPortalAccountRepository))!;

    private static Task<(HttpClient Client, Guid UserId)> CustomerClient(SecurityBaselineFactory factory, string amr, bool withCsrf = false) =>
        Client(factory, "Customer", amr, withCsrf, seedViewer: true);

    private static async Task<(HttpClient Client, Guid UserId)> Client(
        SecurityBaselineFactory factory, string role, string amr, bool withCsrf, bool seedViewer)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        if (seedViewer)
        {
            Repo(factory).Viewer = new PortalViewer(userId, "customer@example.com", "Customer", PortalAccountStatus.Active,
                Guid.NewGuid(), "Cus", "Tomer", Array.Empty<PortalCompanyAccess>());
        }
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: amr);
        var portalCookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.PortalSessionCookieName);
        var client = factory.CreateClient();
        var cookie = $"{portalCookieName}={sessionId}";
        if (withCsrf)
        {
            cookie += $"; {DoubleSubmitCsrfMiddleware.PortalCookieName}=test-csrf-token";
            client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, "test-csrf-token");
        }
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return (client, userId);
    }
}
