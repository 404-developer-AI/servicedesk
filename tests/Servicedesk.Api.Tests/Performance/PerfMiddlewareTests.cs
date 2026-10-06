using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Performance;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;
using Xunit;
using Xunit.Abstractions;

namespace Servicedesk.Api.Tests.Performance;

/// Runs the real performance middleware in a minimal pipeline: routing →
/// perf middleware → fake authentication → pipeline mark → endpoint.
public sealed class PerfMiddlewareTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly InMemorySettingsService _settings = new();
    private PerfSettingsProvider _perf = null!;
    private PerfRecorder _recorder = null!;

    public async Task InitializeAsync()
    {
        _perf = new PerfSettingsProvider(_settings, TimeProvider.System, NullLogger<PerfSettingsProvider>.Instance);
        await _perf.RefreshAsync(default);
        _recorder = new PerfRecorder(TimeProvider.System);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IPerfSettings>(_perf);
        builder.Services.AddSingleton(_recorder);
        _app = builder.Build();
        _app.UseRouting();
        _app.UseMiddleware<PerfHttpMiddleware>();
        _app.Use((ctx, next) =>
        {
            // Stand-in for session authentication: X-Test-Role/X-Test-Amr.
            if (ctx.Request.Headers.TryGetValue("X-Test-Role", out var role))
            {
                var amr = ctx.Request.Headers.TryGetValue("X-Test-Amr", out var a) ? a.ToString() : "pwd";
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, role.ToString()),
                    new Claim(SessionAuthenticationHandler.AmrClaimType, amr),
                }, "test"));
            }
            return next(ctx);
        });
        _app.UseMiddleware<PerfPipelineMarkMiddleware>();
        _app.MapGet("/api/items/{id:guid}", () => Results.Ok(new { data = new string('x', 6000) }));
        _app.MapGet("/api/auth/me", () => Results.Ok(new { ok = true }));
        _app.MapGet("/api/portal/tickets", () => Results.Ok(new { ok = true }));
        _app.MapGet("/api/admin/performance/status", () => Results.Ok(new { ok = true }));
        _app.MapGet("/api/boom", (HttpContext _) => Results.StatusCode(500));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Get(string path, string? role = null, string? amr = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (role is not null) req.Headers.Add("X-Test-Role", role);
        if (amr is not null) req.Headers.Add("X-Test-Amr", amr);
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task Staff_GetServerTiming_WithTimeSplit()
    {
        var res = await Get($"/api/items/{Guid.NewGuid()}", "Agent");
        Assert.True(res.Headers.TryGetValues("Server-Timing", out var values));
        var header = string.Join(",", values!);
        Assert.Contains("total;dur=", header);
        Assert.Contains("db;dur=", header);
        Assert.Contains("ext;dur=", header);
        Assert.Contains("app;dur=", header);
    }

    [Theory]
    [InlineData(null, null)]          // anonymous
    [InlineData("Customer", "pwd+mfa")]
    [InlineData("Agent", "mfa-pending")]  // password step only, MFA still owed
    [InlineData("Customer", "impersonated")]
    public async Task NonStaff_GetNoServerTiming(string? role, string? amr)
    {
        var res = await Get($"/api/items/{Guid.NewGuid()}", role, amr);
        Assert.False(res.Headers.Contains("Server-Timing"));
    }

    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/portal/tickets")]
    public async Task SignInAndPortalEndpoints_NeverGetServerTiming(string path)
    {
        var res = await Get(path, "Admin");
        Assert.False(res.Headers.Contains("Server-Timing"));
    }

    [Fact]
    public async Task Request_IsRecordedByRouteTemplate_WithPayloadSize()
    {
        await Get($"/api/items/{Guid.NewGuid()}", "Agent");
        await Get($"/api/items/{Guid.NewGuid()}", "Agent");
        var window = _recorder.Current;
        var agg = window.Http[new HttpKey("GET", "/api/items/{id:guid}", "2xx")];
        Assert.Equal(2, agg.Count);
        Assert.True(agg.Extras[PerfWindow.HttpBytes] > 2 * 6000);
        Assert.DoesNotContain(window.Http.Keys, k => k.Route.Contains('-')); // no concrete ids
    }

    [Fact]
    public async Task ServerErrors_AreCountedAsFiveHundreds()
    {
        await Get("/api/boom", "Agent");
        var agg = _recorder.Current.Http[new HttpKey("GET", "/api/boom", "5xx")];
        Assert.Equal(1, agg.ErrorCount);
    }

    [Fact]
    public async Task MonitorsOwnEndpoints_AreNotMeasured()
    {
        await Get("/api/admin/performance/status", "Admin");
        Assert.DoesNotContain(_recorder.Current.Http.Keys, k => k.Route.StartsWith("/api/admin/performance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LevelOff_MeasuresNothing_AndSendsNoHeader()
    {
        _settings.Set(SettingKeys.Performance.Level, "off");
        await _perf.RefreshAsync(default);
        _recorder.Swap(DateTimeOffset.UtcNow);

        var res = await Get($"/api/items/{Guid.NewGuid()}", "Agent");

        Assert.False(res.Headers.Contains("Server-Timing"));
        Assert.True(_recorder.Current.Http.IsEmpty);
    }

    [Fact]
    public async Task ServerTimingSetting_Off_KeepsMeasuringWithoutHeader()
    {
        _settings.Set(SettingKeys.Performance.ServerTimingEnabled, "false");
        await _perf.RefreshAsync(default);

        var res = await Get($"/api/items/{Guid.NewGuid()}", "Agent");

        Assert.False(res.Headers.Contains("Server-Timing"));
        Assert.False(_recorder.Current.Http.IsEmpty);
    }
}

public sealed class PerfMiddlewareHelperTests
{
    [Theory]
    [InlineData("/api/tickets/", "/api/tickets")]
    [InlineData("api/tickets/{id:guid}", "/api/tickets/{id:guid}")]
    [InlineData("/", "/")]
    public void Templates_AreNormalised(string raw, string expected)
    {
        Assert.Equal(expected, PerfHttpMiddleware.NormalizeTemplate(raw));
    }

    [Theory]
    [InlineData(200, "2xx")]
    [InlineData(304, "3xx")]
    [InlineData(404, "4xx")]
    [InlineData(429, "429")]
    [InlineData(503, "5xx")]
    public void StatusClasses(int status, string expected) => Assert.Equal(expected, PerfHttpMiddleware.StatusClass(status));

    [Fact]
    public void SpaFallback_AndUnmatched_AreLabelled()
    {
        var ctx = new DefaultHttpContext();
        Assert.Equal("unmatched", PerfHttpMiddleware.RouteLabel(ctx));
        ctx.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("{*path:regex(^(?!api/).*$)}"), 0, null, "fallback"));
        Assert.Equal("spa-fallback", PerfHttpMiddleware.RouteLabel(ctx));
    }
}

public sealed class PerfRumTests
{
    private static PerfRumEndpoints.RumPayload Payload(params PerfRumEndpoints.RumItem[] items) =>
        new() { Device = "desktop", Conn = "4g", Items = items.ToList() };

    private static PerfRumEndpoints.RumItem Item(string m, string r, double v, string? d = null) => new() { M = m, R = r, V = v, D = d };

    [Fact]
    public void ValidItems_AreRecordedPerRouteTemplate()
    {
        var window = new PerfWindow(DateTimeOffset.UtcNow);
        var accepted = PerfRumEndpoints.Ingest(Payload(
            Item("lcp", "/tickets/$ticketId", 2100),
            Item("api_total", "/tickets/$ticketId", 180, "/api/tickets/3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f")), window);

        Assert.Equal(2, accepted);
        Assert.Contains(window.Rum.Keys, k => k.Metric == "api_total" && k.Detail == "/api/tickets/{id}");
    }

    [Fact]
    public void UnknownMetrics_BadValues_AndForeignDetails_AreDropped()
    {
        var window = new PerfWindow(DateTimeOffset.UtcNow);
        var accepted = PerfRumEndpoints.Ingest(Payload(
            Item("cpu_steal", "/x", 1),
            Item("lcp", "/x", -5),
            Item("lcp", "/x", double.PositiveInfinity),
            Item("lcp", "/x", 900_000),
            Item("api_total", "/x", 10, "https://evil.example/collect"),
            Item("loaf", "/x", 80, "<img src=x onerror=alert(1)>")), window);

        Assert.Equal(1, accepted); // only the loaf survives, with its detail replaced
        Assert.Contains(window.Rum.Keys, k => k.Metric == "loaf" && k.Detail == "other");
    }

    [Fact]
    public void Cls_IsScaledIntoTheHistogram_AndDeviceIsWhitelisted()
    {
        var window = new PerfWindow(DateTimeOffset.UtcNow);
        PerfRumEndpoints.Ingest(new PerfRumEndpoints.RumPayload { Device = "toaster", Conn = "<script>", Items = new() { Item("cls", "/", 0.12) } }, window);
        var (key, agg) = Assert.Single(window.Rum);
        Assert.Equal("other", key.Device);
        Assert.Equal("unknown", key.Connection);
        Assert.Equal(120, agg.MaxValue, 3);
    }
}

public sealed class PerfSignalRTests
{
    private sealed class TestHub : Hub { }

    /// The broadcast-counting decorator must forward *every* overridable
    /// member — a missed one would silently fall back to the abstract base
    /// (throwing) for client results or group management.
    [Fact]
    public void LifetimeManagerDecorator_OverridesEveryMember()
    {
        var baseType = typeof(HubLifetimeManager<TestHub>);
        var decorator = typeof(PerfHubLifetimeManager<TestHub>);
        var overridable = baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => (m.IsAbstract || m.IsVirtual) && !m.IsFinal && m.DeclaringType == baseType);
        foreach (var method in overridable)
        {
            var impl = decorator.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == method.Name
                    && m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(method.GetParameters().Select(p => p.ParameterType.Name)));
            Assert.True(impl is not null && impl.DeclaringType == decorator, $"{method.Name} is not overridden");
        }
    }
}

public sealed class PerfOverheadTests
{
    private readonly ITestOutputHelper _output;

    public PerfOverheadTests(ITestOutputHelper output) => _output = output;

    /// Rough benchmark of the request-path cost (middleware bookkeeping +
    /// aggregate update). Targets: Basic < 50 µs, Diagnose < 300 µs per
    /// request; asserted with headroom so a slow CI runner does not flake.
    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 400)]
    public async Task MiddlewareOverhead_StaysWithinBudget(bool diagnose, double limitMicros)
    {
        var settings = new InMemorySettingsService();
        if (diagnose) settings.Set(SettingKeys.Performance.DiagnoseUntilUtc, PerfSettingsProvider.FormatUtc(DateTimeOffset.UtcNow.AddHours(1)));
        var perf = new PerfSettingsProvider(settings, TimeProvider.System, NullLogger<PerfSettingsProvider>.Instance);
        await perf.RefreshAsync(default);
        var recorder = new PerfRecorder(TimeProvider.System);
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/tickets/{id:guid}"), 0, null, "t");
        var withPerf = new PerfHttpMiddleware(_ => Task.CompletedTask, recorder, perf);

        static DefaultHttpContext NewContext(Endpoint e)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/api/tickets/3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f";
            ctx.SetEndpoint(e);
            return ctx;
        }

        const int n = 20_000;
        for (var i = 0; i < 2000; i++) await withPerf.InvokeAsync(NewContext(endpoint)); // warm-up

        var baseline = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) _ = NewContext(endpoint);
        baseline.Stop();

        var measured = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) await withPerf.InvokeAsync(NewContext(endpoint));
        measured.Stop();

        var perRequestMicros = Math.Max(0, (measured.Elapsed - baseline.Elapsed).TotalMicroseconds / n);
        _output.WriteLine($"{(diagnose ? "Diagnose" : "Basic")}: {perRequestMicros:0.00} µs per request");
        Assert.True(perRequestMicros < limitMicros, $"overhead {perRequestMicros:0.00} µs");
    }
}
