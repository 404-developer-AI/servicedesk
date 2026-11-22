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

/// A per-user feature flag is an access boundary, enforced server-side
/// (found 2026-10-09: these routes served data to any agent regardless of
/// the flag). Every route behind a flag answers 403 for an agent with the
/// flag off — the handler never runs, so no DB is needed.
public sealed class FeatureFlagEnforcementTests
{
    [Theory]
    // assets_enabled
    [InlineData("GET", "/api/assets/")]
    [InlineData("GET", "/api/assets/builds")]
    [InlineData("GET", "/api/assets/sync-state")]
    [InlineData("GET", "/api/assets/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/assets/remote-desktop/")]
    [InlineData("GET", "/api/assets/remote-desktop/export.csv")]
    [InlineData("GET", "/api/assets/remote-desktop/clients/1/notes")]
    // adsolut_orders_enabled
    [InlineData("GET", "/api/orders")]
    [InlineData("GET", "/api/orders/picker?q=a")]
    [InlineData("GET", "/api/orders/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/orders/sync")]
    [InlineData("GET", "/api/tickets/00000000-0000-0000-0000-000000000001/orders")]
    // adsolut_timesheet_enabled
    [InlineData("GET", "/api/timesheet/adsolut/receipts")]
    [InlineData("GET", "/api/timesheet/adsolut/receipts/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/timesheet/adsolut/receipts/00000000-0000-0000-0000-000000000001/hours")]
    // timesheet_backoffice_enabled
    [InlineData("GET", "/api/timesheet/backoffice/resolved?year=2026&month=9")]
    [InlineData("GET", "/api/timesheet/backoffice/cwi?year=2026&month=9")]
    [InlineData("GET", "/api/timesheet/backoffice/tickets/00000000-0000-0000-0000-000000000001/hours")]
    // timesheet_enabled
    [InlineData("GET", "/api/timesheet/entries/?date=2026-10-13")]
    // backoffice_forms_enabled
    [InlineData("GET", "/api/backoffice-forms/")]
    [InlineData("GET", "/api/backoffice-forms/counts")]
    [InlineData("GET", "/api/backoffice-forms/templates")]
    [InlineData("GET", "/api/backoffice-forms/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/backoffice-forms/presence")]
    [InlineData("GET", "/api/backoffice-forms/ticket/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/backoffice-forms/00000000-0000-0000-0000-000000000001/handle")]
    [InlineData("POST", "/api/backoffice-forms/00000000-0000-0000-0000-000000000001/reopen")]
    // calendar_planning_enabled (v0.1.61 — Outlook planning from forms)
    [InlineData("GET", "/api/calendar/availability?userIds=00000000-0000-0000-0000-000000000001&start=2026-11-23T08:00:00Z&end=2026-11-23T10:00:00Z")]
    // order_management_enabled (v0.1.59 — TD SYNNEX orders)
    [InlineData("GET", "/api/order-management/summary")]
    [InlineData("GET", "/api/order-management/orders")]
    [InlineData("GET", "/api/order-management/orders/picker?q=a")]
    [InlineData("GET", "/api/order-management/orders/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/order-management/sync")]
    [InlineData("GET", "/api/tickets/00000000-0000-0000-0000-000000000001/tdsynnex-orders")]
    [InlineData("POST", "/api/tickets/00000000-0000-0000-0000-000000000001/tdsynnex-orders/00000000-0000-0000-0000-000000000002")]
    [InlineData("DELETE", "/api/tickets/00000000-0000-0000-0000-000000000001/tdsynnex-orders/00000000-0000-0000-0000-000000000002")]
    public async Task Agent_without_flag_gets_403(string method, string url)
    {
        using var factory = new SecurityBaselineFactory();
        var (client, _) = await ClientAsync(factory, "Agent");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Admin")]
    public async Task The_flag_alone_decides_whatever_the_role(string role)
    {
        using var factory = new SecurityBaselineFactory();
        var (client, userId) = await ClientAsync(factory, role);

        var off = await client.GetAsync("/api/assets/sync-state");
        factory.Users.AssetsEnabled[userId] = true;

        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        // Past the gate the handler runs and fails on the test host's
        // unreachable database — what matters is that it is no longer a 403.
        try
        {
            var on = await client.GetAsync("/api/assets/sync-state");
            Assert.NotEqual(HttpStatusCode.Forbidden, on.StatusCode);
        }
        catch (NpgsqlException)
        {
            // Reached the repository: the gate let the request through.
        }
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("cwi")]
    public async Task Backoffice_checks_need_the_backoffice_flag(string context)
    {
        using var factory = new SecurityBaselineFactory();
        var (client, userId) = await ClientAsync(factory, "Agent");
        factory.Users.AdsolutTimesheetEnabled[userId] = true; // the other flag doesn't count

        var response = await client.PostAsJsonAsync("/api/timesheet/backoffice/checks",
            new { context, ids = new[] { Guid.NewGuid() }, @checked = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Adsolut_checks_need_the_adsolut_timesheet_flag()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, userId) = await ClientAsync(factory, "Agent");
        factory.Users.TimesheetBackofficeEnabled[userId] = true; // the other flag doesn't count

        var response = await client.PostAsJsonAsync("/api/timesheet/backoffice/checks",
            new { context = "adsolut", ids = new[] { Guid.NewGuid() }, @checked = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Flag_lookup_maps_each_feature_to_its_own_flag()
    {
        var users = new FakeUserService();
        var userId = Guid.NewGuid();
        users.AssetsEnabled[userId] = true;
        users.TimesheetManagers[userId] = true; // fake returns timesheet Enabled = true

        Assert.True(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.Assets, default));
        Assert.True(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.Timesheet, default));
        Assert.False(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.AdsolutOrders, default));
        Assert.False(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.AdsolutTimesheet, default));
        Assert.False(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.TimesheetBackoffice, default));
        Assert.False(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.BackofficeForms, default));

        users.BackofficeFormsEnabled[userId] = true;
        Assert.True(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.BackofficeForms, default));
        Assert.False(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.TimesheetBackoffice, default));

        Assert.False(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.CalendarPlanning, default));
        users.CalendarPlanningEnabled[userId] = true;
        Assert.True(await FeatureFlagFilter.IsEnabledAsync(users, userId, UserFeature.CalendarPlanning, default));
    }

    [Fact]
    public async Task Backoffice_forms_need_their_own_flag_also_for_admins()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, userId) = await ClientAsync(factory, "Admin");
        factory.Users.TimesheetBackofficeEnabled[userId] = true; // the timesheet back-office flag doesn't count

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/backoffice-forms/counts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            "/api/backoffice-forms/00000000-0000-0000-0000-000000000001/handle", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task Order_management_needs_its_own_flag_also_for_admins()
    {
        using var factory = new SecurityBaselineFactory();
        var (client, userId) = await ClientAsync(factory, "Admin");
        factory.Users.AdsolutOrdersEnabled[userId] = true; // the Adsolut orders flag doesn't count

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/order-management/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(
            "/api/tickets/00000000-0000-0000-0000-000000000001/tdsynnex-orders/00000000-0000-0000-0000-000000000002", null)).StatusCode);

        factory.Users.OrderManagementEnabled[userId] = true;
        Assert.True(await FeatureFlagFilter.IsEnabledAsync(factory.Users, userId, UserFeature.OrderManagement, default));
        Assert.False(await FeatureFlagFilter.IsEnabledAsync(factory.Users, Guid.NewGuid(), UserFeature.OrderManagement, default));
    }

    [Fact]
    public async Task TdSynnex_order_search_gives_zero_hits_without_the_flag()
    {
        // null! data source: an unavailable principal must never reach the DB.
        var src = new TdSynnexOrderSearchSource(null!);
        var plainAgent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var plainAdmin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.False(src.IsAvailableFor(plainAgent));
        Assert.False(src.IsAvailableFor(plainAdmin)); // the flag is the boundary, admins included
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Customer", null,
            new HashSet<string> { SearchFeature.OrderManagement })));
        var result = await src.SearchAsync(new SearchRequest("I00123", null, 10, 0), plainAgent, default);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);

        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(),
            new HashSet<string> { SearchFeature.OrderManagement })));
    }

    // ---- global search: Assets source follows the same flag -----------------

    [Fact]
    public async Task Asset_search_gives_agent_without_flag_zero_hits()
    {
        // null! data source: an unavailable principal must never reach the DB.
        var src = new AssetSearchSource(null!);
        var plainAgent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());

        Assert.False(src.IsAvailableFor(plainAgent));
        var result = await src.SearchAsync(new SearchRequest("srv", null, 10, 0), plainAgent, default);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TotalInGroup);
    }

    [Fact]
    public void Asset_search_is_available_to_flagged_agent_and_admin_only()
    {
        var src = new AssetSearchSource(null!);
        var flaggedAgent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>(),
            new HashSet<string> { SearchFeature.Assets });

        Assert.True(src.IsAvailableFor(flaggedAgent));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Customer", null)));
    }

    private static async Task<(HttpClient Client, Guid UserId)> ClientAsync(
        SecurityBaselineFactory factory, string role)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        // Handlers are bound (and their services resolved) before the endpoint
        // filter runs, so the host needs a data source — an unreachable one,
        // since a gated request must never reach it.
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        }));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return (client, userId);
    }
}
