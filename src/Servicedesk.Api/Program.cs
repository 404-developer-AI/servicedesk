using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using Serilog;
using Servicedesk.Api.Audit;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Companies;
using Servicedesk.Api.Health;
using Servicedesk.Api.Security;
using Servicedesk.Api.System;
using Servicedesk.Api.Taxonomy;
using Servicedesk.Api.Tickets;
using Servicedesk.Api.Settings;
using Servicedesk.Api.Access;
using Servicedesk.Api.Views;
using Servicedesk.Api.Users;
using Servicedesk.Api.Preferences;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure;
using Servicedesk.Infrastructure.Settings;

var builder = WebApplication.CreateBuilder(args);

// Strip the default "Server: Kestrel" banner. Middleware-level header removal
// runs too late — Kestrel writes it via its own response writer.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

builder.Configuration.AddEnvironmentVariables(prefix: "SERVICEDESK_");
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Servicedesk API", Version = "v1" });
});

// Trust the X-Forwarded-For header from nginx so RemoteIpAddress is correct.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// Data Protection keyring lives in Postgres, AES-GCM encrypted with a master
// key from DataProtection:MasterKey. Wired inside AddServicedeskInfrastructure.
builder.Services.AddServicedeskInfrastructure(builder.Configuration);

// Cookie-backed session auth. The handler reads an opaque session id from the
// cookie, validates it against the Postgres session store, and hydrates
// ClaimsPrincipal for downstream authorization policies.
builder.Services.AddMemoryCache();
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options => options.AddServicedeskPolicies());
builder.Services.AddSignalR();

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = AuditRateLimiterEvents.OnRejected;

    // Values read from configuration at startup. Live-reload on change is a
    // v0.0.x concern — see Security.RateLimit.* keys in SettingKeys.
    var globalPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Global:PermitPerWindow") ?? 120;
    var globalWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Global:WindowSeconds") ?? 60;
    var authPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Auth:PermitPerWindow") ?? 10;
    var authWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Auth:WindowSeconds") ?? 60;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = globalPermit,
            Window = TimeSpan.FromSeconds(globalWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    options.AddPolicy("auth", ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermit,
            Window = TimeSpan.FromSeconds(authWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    options.AddPolicy("csp-report", ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseServicedeskSecurityHeaders();
app.UseServicedeskContentSecurityPolicy();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<DoubleSubmitCsrfMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var systemInfo = SystemInfo.Capture(Assembly.GetExecutingAssembly());

app.MapGet("/api/system/version", () => Results.Ok(new
{
    version = systemInfo.Version,
    commit = systemInfo.Commit,
    buildTime = systemInfo.BuildTime
}))
.WithName("GetSystemVersion")
.WithOpenApi();

app.MapGet("/api/system/time", () =>
{
    var nowUtc = DateTimeOffset.UtcNow;
    return Results.Ok(new
    {
        utc = nowUtc,
        timezone = TimeZoneInfo.Local.Id,
        offsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(nowUtc.UtcDateTime).TotalMinutes
    });
})
.WithName("GetSystemTime")
.WithOpenApi();

app.MapCspReportEndpoint();
app.MapAuditEndpoints();
app.MapAuthEndpoints();
app.MapTaxonomyEndpoints();
app.MapCompanyEndpoints();
app.MapTicketEndpoints();
app.MapSettingEndpoints();
app.MapGraphAdminEndpoints();
app.MapHealthEndpoints();
app.MapViewEndpoints();
app.MapQueueAccessEndpoints();
app.MapViewGroupEndpoints();
app.MapViewAccessEndpoints();
app.MapAgentQueueEndpoints();
app.MapUserEndpoints();
app.MapUserPreferencesEndpoints();
app.MapDevBenchmarkEndpoints(app.Environment);
app.MapHub<TicketPresenceHub>("/hubs/presence");

app.Run();

public partial class Program { }
