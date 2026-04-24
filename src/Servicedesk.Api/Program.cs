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
using Servicedesk.Api.IntakeForms;
using Servicedesk.Api.Security;
using Servicedesk.Api.System;
using Servicedesk.Api.Taxonomy;
using Servicedesk.Api.Search;
using Servicedesk.Api.Tickets;
using Servicedesk.Api.Settings;
using Servicedesk.Api.Sla;
using Servicedesk.Api.Access;
using Servicedesk.Api.Views;
using Servicedesk.Api.Users;
using Servicedesk.Api.Preferences;
using Servicedesk.Api.Presence;
using Servicedesk.Api.Notifications;
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
    .WriteTo.Console()
    .WriteTo.Sink(new Servicedesk.Infrastructure.Observability.IncidentLogSink()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Servicedesk API", Version = "v1" });
});

// Trust the X-Forwarded-For + X-Forwarded-Proto headers from nginx so
// Connection.RemoteIpAddress + Request.IsHttps reflect the real client.
//
// We Clear() the defaults (which trust loopback only) and explicitly trust
// the docker compose bridge — pinned to 172.28.0.0/16 in
// deploy/docker-compose.yml. Without this allow-list ASP.NET silently
// drops the XFF chain because nginx's container IP isn't in the trust
// list, leaving every audit-log row tagged with the bridge gateway IP
// instead of the real client. The subnet is overridable via
// SERVICEDESK_ForwardedHeaders__TrustedNetwork for installs that re-pin
// the bridge; defaults to the install.sh-managed value.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();

    var trustedCidr = builder.Configuration["ForwardedHeaders:TrustedNetwork"] ?? "172.28.0.0/16";
    if (TryParseCidr(trustedCidr, out var address, out var prefix))
    {
        o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix));
    }

    // Allow up to 2 hops in the XFF chain so a future inline-CDN
    // (Cloudflare, etc.) doesn't have to be wired in here as well.
    o.ForwardLimit = 2;

    static bool TryParseCidr(string cidr, out System.Net.IPAddress address, out int prefix)
    {
        address = System.Net.IPAddress.None;
        prefix = 0;
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2) return false;
        if (!System.Net.IPAddress.TryParse(parts[0], out var addr)) return false;
        if (!int.TryParse(parts[1], out var p) || p < 0 || p > 32) return false;
        address = addr;
        prefix = p;
        return true;
    }
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
// Override the Infrastructure-layer default (no-op) with the SignalR-backed
// ticket-list notifier so background services (mail ingest) can push updates.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.ITicketListNotifier,
    Servicedesk.Api.Presence.SignalRTicketListNotifier>();
// v0.0.12 stap 4 — same trick for per-user notifications.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.IUserNotifier,
    Servicedesk.Api.Presence.SignalRUserNotifier>();
// v0.0.18 — security-activity health-subsystem pushes alerts to all
// active Admins via the same UserNotificationHub, using a different
// SignalR method name ("SecurityAlertReceived") so the existing
// mention-notification handler shape is left untouched.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.ISecurityAlertNotifier,
    Servicedesk.Api.Presence.SignalRSecurityAlertNotifier>();

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

    // Intake Forms public endpoints (v0.0.19). Partitioned per (IP, token)
    // so one IP probing many tokens is throttled per-token AND a single
    // legitimate customer reloading their form stays well under the cap.
    // Defaults to 20 req / 60s — tunable via IntakeForms.PublicRateLimit.*
    // setting keys in the settings DB (rate-limit config is loaded at
    // startup and not live-reloaded, matching the global + auth policies).
    var intakePublicPermit = builder.Configuration.GetValue<int?>("IntakeForms:PublicRateLimit:PermitPerWindow") ?? 20;
    var intakePublicWindow = builder.Configuration.GetValue<int?>("IntakeForms:PublicRateLimit:WindowSeconds") ?? 60;

    options.AddPolicy("intake-public", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        // Extract the token path segment so the partition key is
        // {ip}:{token-prefix}. The token is opaque to us; slicing at 32
        // chars keeps the key short without collapsing distinct tokens.
        var path = ctx.Request.Path.Value ?? string.Empty;
        string tokenPart = "-";
        const string prefix = "/api/intake-forms/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            var token = slash < 0 ? rest : rest[..slash];
            if (token.Length > 32) token = token[..32];
            if (!string.IsNullOrEmpty(token)) tokenPart = token;
        }

        var key = ip + ":" + tokenPart;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = intakePublicPermit,
            Window = TimeSpan.FromSeconds(intakePublicWindow),
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

// Serve the React SPA bundle from /wwwroot in production. In dev the folder
// is empty (Vite serves the UI on :5173 via proxy), so this is a no-op.
//
// Order matters: StaticFileMiddleware has a ValidateNoEndpoint check that
// skips serving when routing has already matched an endpoint. If we let
// WebApplication auto-insert UseRouting at the start of the pipeline, the
// MapFallbackToFile catch-all matches FIRST — then UseStaticFiles sees the
// matched endpoint and passes through, so every /assets/*.js request returns
// index.html with text/html. Calling UseRouting() explicitly AFTER
// UseStaticFiles moves routing past the static-file check, so real files on
// disk are served before the fallback endpoint is considered.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

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

// Display-timezone resolution order:
//   1. `App.TimeZone` setting (IANA id). Admin can override from Settings → General.
//   2. Container `TZ` env-var → surfaces here as TimeZoneInfo.Local. install.sh
//      writes this from the host timezone so a fresh install already reflects
//      the admin's local clock without a trip through the Settings page.
//   3. UTC hard floor if both fail.
// An invalid IANA id or a DB-read hiccup silently falls back to step 2/3 so the
// endpoint never 500s — clients depend on it for every live-clock tick.
app.MapGet("/api/system/time", async (ISettingsService settings, CancellationToken ct) =>
{
    string configured = string.Empty;
    try
    {
        configured = await settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
    }
    catch
    {
        // Settings store unreachable — drop through to the host default below.
    }

    var tz = ResolveDisplayTimeZone(configured);
    var nowUtc = DateTimeOffset.UtcNow;
    return Results.Ok(new
    {
        utc = nowUtc,
        timezone = tz.Id,
        offsetMinutes = (int)tz.GetUtcOffset(nowUtc.UtcDateTime).TotalMinutes
    });

    static TimeZoneInfo ResolveDisplayTimeZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* Invalid IANA id — fall through. */ }
        }
        return TimeZoneInfo.Local;
    }
})
.WithName("GetSystemTime")
.WithOpenApi();

app.MapCspReportEndpoint();
app.MapAuditEndpoints();
app.MapAuthEndpoints();
app.MapMicrosoftAuthEndpoints();
app.MapAdminUserEndpoints();
app.MapTaxonomyEndpoints();
app.MapCompanyEndpoints();
app.MapTicketEndpoints();
app.MapTicketExportEndpoints();
app.MapTicketMailEndpoints();
app.MapTicketAttachmentEndpoints();
app.MapSearchEndpoints();
app.MapSettingEndpoints();
app.MapSlaEndpoints();
app.MapIntakeFormEndpoints();
app.MapGraphAdminEndpoints();
app.MapAdminMailDiagnosticsEndpoints();
app.MapHealthEndpoints();
app.MapViewEndpoints();
app.MapQueueAccessEndpoints();
app.MapViewGroupEndpoints();
app.MapViewAccessEndpoints();
app.MapAgentQueueEndpoints();
app.MapUserEndpoints();
app.MapUserPreferencesEndpoints();
app.MapNotificationEndpoints();
app.MapDevBenchmarkEndpoints(app.Environment);
app.MapHub<TicketPresenceHub>("/hubs/presence");
app.MapHub<UserNotificationHub>("/hubs/notifications");

// Deep-link fallback for the SPA. The regex excludes /api/* and /hubs/* so an
// unknown API route still returns 404 (JSON client) instead of HTML.
app.MapFallbackToFile("{*path:regex(^(?!api/|hubs/).*$)}", "index.html");

app.Run();

public partial class Program { }
