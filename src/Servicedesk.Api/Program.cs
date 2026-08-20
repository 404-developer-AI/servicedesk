using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using Serilog;
using Servicedesk.Api.Audit;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Portal;
using Servicedesk.Api.Companies;
using Servicedesk.Api.ComposeTemplates;
using Servicedesk.Api.TicketTemplates;
using Servicedesk.Api.TaggingMailboxes;
using Servicedesk.Api.Signatures;
using Servicedesk.Api.Activity;
using Servicedesk.Api.Dashboard;
using Servicedesk.Api.Health;
using Servicedesk.Api.IntakeForms;
using Servicedesk.Api.Contracts;
using Servicedesk.Api.Feedback;
using Servicedesk.Api.Integrations;
using Servicedesk.Api.KnowledgeBase;
using Servicedesk.Api.Orders;
using Servicedesk.Api.Reporting;
using Servicedesk.Api.Timesheet;
using Servicedesk.Api.Security;
using Servicedesk.Api.System;
using Servicedesk.Api.Taxonomy;
using Servicedesk.Api.Search;
using Servicedesk.Api.Statistics;
using Servicedesk.Api.Checklists;
using Servicedesk.Api.Tickets;
using Servicedesk.Api.Settings;
using Servicedesk.Api.Sla;
using Servicedesk.Api.Surveys;
using Servicedesk.Api.Iso27001;
using Servicedesk.Api.Triggers;
using Servicedesk.Api.Access;
using Servicedesk.Api.Views;
using Servicedesk.Api.Users;
using Servicedesk.Api.Preferences;
using Servicedesk.Api.Presence;
using Servicedesk.Api.Notifications;
using Servicedesk.Domain.Tickets;
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
    // Use the full type name as the schema id so distinct types that share a
    // short name (e.g. Telavox/Adsolut SetSecretRequest) don't collide and
    // break Swagger generation.
    c.CustomSchemaIds(type => type.FullName);
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

// install.sh pins SERVICEDESK_AllowedHosts to the public domain, but the
// Docker HEALTHCHECK calls http://localhost:8080/api/system/health from
// inside the container. Without this, Kestrel's HostFiltering rejects the
// loopback Host header with 400 and the container never reaches "healthy".
// Loopback is only reachable from inside the container, so always-listing
// it does not widen the public attack surface.
//
// Must be PostConfigure (runs after every IConfigureOptions) so the
// framework's own HostFilteringOptionsSetup has already populated the list
// from SERVICEDESK_AllowedHosts. That setup assigns `options.AllowedHosts =
// hosts.Split(';')`, which is a fixed-size `string[]` exposed as
// `IList<string>`. Mutating it in place throws NotSupportedException at
// startup (v0.0.47 regression), so we always rebuild into a fresh
// List<string> instead.
builder.Services.PostConfigure<HostFilteringOptions>(o =>
{
    var merged = new List<string>(o.AllowedHosts);
    foreach (var loopback in new[] { "localhost", "127.0.0.1", "[::1]" })
    {
        if (!merged.Contains(loopback)) merged.Add(loopback);
    }
    o.AllowedHosts = merged;
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
// v0.0.25 — integration-framework healthcheck pushes status changes to
// every connected admin via a dedicated IntegrationsHub.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.IIntegrationStatusNotifier,
    Servicedesk.Api.Presence.SignalRIntegrationStatusNotifier>();
// v0.0.34 commit C — Telavox call-popup polling worker pushes per-agent
// call events via the dedicated TelavoxCallHub.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.ITelavoxCallNotifier,
    Servicedesk.Api.Presence.SignalRTelavoxCallNotifier>();
// v0.0.35 commit H — timesheet entry CUD pushes to both the
// timesheet-managers group (Tab 2 / Tab 3) and the ticket:{id} group
// (TicketTimesheetPanel) via the existing TicketPresenceHub.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.ITimesheetEntryNotifier,
    Servicedesk.Api.Presence.SignalRTimesheetEntryNotifier>();
// v0.0.84 — Timesheet → Comments per-user push (TimesheetCommentReceived on
// the same UserNotificationHub the mention raamwerk uses).
builder.Services.AddSingleton<Servicedesk.Infrastructure.Realtime.ITimesheetCommentNotifier,
    Servicedesk.Api.Presence.SignalRTimesheetCommentNotifier>();
// v0.0.52 — Tactical RMM sync worker emits an AssetsChanged ping on
// TicketPresenceHub so the Assets page invalidates after every cycle.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Integrations.Trmm.ITrmmSyncNotifier,
    Servicedesk.Api.Presence.SignalRTrmmSyncNotifier>();
// v0.0.42 — dashboard AgentActivity tile broadcaster. Sends per-user
// activity updates to the agent-activity-admins SignalR group. Used by
// TicketPresenceHub (presence transitions) and TelavoxPollingWorker
// (call-state transitions) so the tile updates from a single channel.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Dashboard.IAgentActivityBroadcaster,
    Servicedesk.Api.Presence.SignalRAgentActivityBroadcaster>();

// v0.0.42 — Agent activity feed SignalR push. Implementation lives in
// the API project because it needs IHubContext<ActivityFeedHub>; the
// Infrastructure ActivityListenerWorker resolves IActivityBroadcaster
// from a scope and routes the enriched row here.
builder.Services.AddSingleton<Servicedesk.Infrastructure.Activity.IActivityBroadcaster,
    Servicedesk.Api.Activity.SignalRActivityBroadcaster>();

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = AuditRateLimiterEvents.OnRejected;

    // Values read from configuration at startup. Live-reload on change is a
    // v0.0.x concern — see Security.RateLimit.* keys in SettingKeys.
    // 240/60s per IP: a single content-rich page (a ticket with many inline
    // images, each a separate authenticated GET) legitimately fires dozens of
    // requests at once, so the old 120 tripped on normal use. Still per-IP and
    // bounded; tune via Security:RateLimit:Global:* without a rebuild.
    var globalPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Global:PermitPerWindow") ?? 240;
    var globalWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Global:WindowSeconds") ?? 60;
    // Inline-image GETs get their own generous bucket: one content-rich ticket
    // (a long imported email thread) can embed 150+ inline images, each a
    // separate authenticated GET fired at once on open. They're queue-access
    // gated and ETag-cached (304 on revisit), so the tight API budget doesn't
    // apply — but a per-IP ceiling still bounds abuse.
    var attachmentPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Attachments:PermitPerWindow") ?? 1200;
    var attachmentWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Attachments:WindowSeconds") ?? 60;
    var authPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Auth:PermitPerWindow") ?? 10;
    var authWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Auth:WindowSeconds") ?? 60;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        // The always-on lightweight polls — the live server clock
        // (/api/system/time) and the dashboard status pill
        // (/api/system/health) — must never be throttled. They drive
        // globally-visible UI and are cheap, read-only, and carry no attack
        // surface worth limiting. Without this, a burst elsewhere on the same
        // IP (e.g. a content-heavy ticket) exhausts the window and freezes the
        // whole UI's clock/status. /api/system/version is deliberately NOT
        // exempt — it's fetched once per load plus event-driven re-checks
        // (SignalR reconnect / tab focus, client-throttled), never on a timer.
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/system/time", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/system/health", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("system-poll");
        }

        // Inline-image / attachment downloads (GET …/attachments/{id}) run on
        // their own generous per-IP bucket so an image-heavy ticket doesn't
        // exhaust the main API budget and 429 the rest of the page.
        if (HttpMethods.IsGet(ctx.Request.Method)
            && path.Contains("/attachments/", StringComparison.OrdinalIgnoreCase))
        {
            var attKey = "att:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "anon");
            return RateLimitPartition.GetFixedWindowLimiter(attKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = attachmentPermit,
                Window = TimeSpan.FromSeconds(attachmentWindow),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }

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

    // v0.0.92 — was a hardcoded 20/min; the default is now 60/min and tunable
    // like the other policies. With inline-script hashes in the CSP and report
    // dedup in place a healthy install sends almost no reports, so 60 is pure
    // headroom for e.g. a misbehaving browser extension across many tabs.
    var cspReportPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:CspReport:PermitPerWindow") ?? 60;
    var cspReportWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:CspReport:WindowSeconds") ?? 60;
    options.AddPolicy("csp-report", ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = cspReportPermit,
            Window = TimeSpan.FromSeconds(cspReportWindow),
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

    // v0.0.38 — public survey endpoints partition by {ip, token} just like
    // intake-public. Defaults to 10 req / 60s (stricter than intake because
    // a GET+POST is the only legitimate flow; reloads are rare).
    var surveyPublicPermit = builder.Configuration.GetValue<int?>("Surveys:PublicRateLimit:PermitPerWindow") ?? 10;
    var surveyPublicWindow = builder.Configuration.GetValue<int?>("Surveys:PublicRateLimit:WindowSeconds") ?? 60;
    options.AddPolicy("survey-public", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        var path = ctx.Request.Path.Value ?? string.Empty;
        string tokenPart = "-";
        const string prefix = "/api/public/surveys/";
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
            PermitLimit = surveyPublicPermit,
            Window = TimeSpan.FromSeconds(surveyPublicWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // v0.0.75 — public KB article reader partitions by {ip, articleId}.
    // More permits than surveys because rendering one article also pulls
    // its inline images through the public attachment route.
    var kbPublicPermit = builder.Configuration.GetValue<int?>("KnowledgeBase:PublicRateLimit:PermitPerWindow") ?? 60;
    var kbPublicWindow = builder.Configuration.GetValue<int?>("KnowledgeBase:PublicRateLimit:WindowSeconds") ?? 60;
    options.AddPolicy("kb-public", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        var path = ctx.Request.Path.Value ?? string.Empty;
        string articlePart = "-";
        const string prefix = "/api/public/kb/articles/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            var token = slash < 0 ? rest : rest[..slash];
            if (token.Length > 36) token = token[..36];
            if (!string.IsNullOrEmpty(token)) articlePart = token;
        }

        var key = ip + ":" + articlePart;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = kbPublicPermit,
            Window = TimeSpan.FromSeconds(kbPublicWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // v0.0.96 — Reporting API (key-gated machine-to-machine statistics).
    // Per-IP: the caller is one external tool, not a browser fanning out
    // requests, so a tight budget costs nothing and bounds both key
    // guessing and audit-log noise from denied probes.
    // v0.0.102 — Bulk ticket actions. Per authenticated user (falls back to
    // IP): one bulk call is N single-ticket mutations, so a tight budget
    // stops a runaway client from hammering triggers/mail through the
    // ticket API in a way the global per-IP limit would never notice.
    var bulkPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Bulk:PermitPerWindow") ?? 20;
    var bulkWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Bulk:WindowSeconds") ?? 60;
    options.AddPolicy("bulk", ctx =>
    {
        var key = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = bulkPermit,
            Window = TimeSpan.FromSeconds(bulkWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    var reportingPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:Reporting:PermitPerWindow") ?? 30;
    var reportingWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:Reporting:WindowSeconds") ?? 60;
    options.AddPolicy("reporting", ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = reportingPermit,
            Window = TimeSpan.FromSeconds(reportingWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // v0.1.0 — customer portal. Two anonymous buckets, both per IP and
    // separate from the agent "auth" bucket so a busy customer office behind
    // one NAT cannot lock agents out (or vice versa):
    //   portal-auth     — login / 2FA / reset / invitation accept (10/min)
    //   portal-register — registration + forgot-password (5 per 10 min):
    //                     both send mail, so they are throttled harder.
    // Settings → Portal shows the display-only mirrors (Portal.RateLimit.*).
    var portalAuthPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:PortalAuth:PermitPerWindow") ?? 10;
    var portalAuthWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:PortalAuth:WindowSeconds") ?? 60;
    options.AddPolicy("portal-auth", ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = portalAuthPermit,
            Window = TimeSpan.FromSeconds(portalAuthWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    var portalRegisterPermit = builder.Configuration.GetValue<int?>("Security:RateLimit:PortalRegister:PermitPerWindow") ?? 5;
    var portalRegisterWindow = builder.Configuration.GetValue<int?>("Security:RateLimit:PortalRegister:WindowSeconds") ?? 600;
    options.AddPolicy("portal-register", ctx =>
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = portalRegisterPermit,
            Window = TimeSpan.FromSeconds(portalRegisterWindow),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var app = builder.Build();

var systemInfo = SystemInfo.Capture(Assembly.GetExecutingAssembly());

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
// index.html must always be revalidated: after a deploy the hashed asset
// files it references are gone, so a browser-cached copy would boot a
// broken shell. no-cache means "revalidate before use" (ETag round-trip,
// 304 when unchanged).
// Everything under /assets/ is content-hashed by Vite (name-<hash>.ext), so
// a URL never changes meaning: cache it for a year and mark it immutable
// (v0.0.101) — no conditional requests on every reload/tab. Other root files
// (favicon etc.) keep the default heuristic caching.
var indexNoCache = new Action<Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext>(ctx =>
{
    if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
    else if (ctx.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }
});
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = indexNoCache });
app.UseRouting();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<DoubleSubmitCsrfMiddleware>();
// v0.1.1 — refuses every write on the customer portal surface while the
// session is an admin's read-only shadow view (amr "impersonated").
app.UseMiddleware<Servicedesk.Api.Portal.PortalImpersonationReadOnlyMiddleware>();
app.UseMiddleware<ClientVersionGateMiddleware>(systemInfo);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Besides identifying the build, this endpoint carries the update-refresh
// behavior settings so every client (customers included — the endpoint is
// anonymous) learns in the same fetch how to react to a version mismatch.
// Setting reads are served from the settings cache and fall back to safe
// defaults so the endpoint never 500s — CI's boot probe depends on it.
app.MapGet("/api/system/version", async (ISettingsService settings, CancellationToken ct) =>
{
    var refreshMode = "auto";
    var checkOnFocus = true;
    try
    {
        refreshMode = await settings.GetAsync<string>(SettingKeys.App.UpdateRefreshMode, ct);
        checkOnFocus = await settings.GetAsync<bool>(SettingKeys.App.UpdateCheckOnFocus, ct);
    }
    catch
    {
        // Settings store unreachable — report the defaults above.
    }

    return Results.Ok(new
    {
        version = systemInfo.Version,
        commit = systemInfo.Commit,
        buildTime = systemInfo.BuildTime,
        updateRefreshMode = refreshMode,
        updateCheckOnFocus = checkOnFocus
    });
})
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

// Maintenance-window read endpoint. Public: the login page (no auth) needs
// the same banner the authenticated app shows. Server is the single source
// of truth — `active` is computed from current UTC vs EndUtc so the banner
// disappears on its own without any admin action. Empty/invalid datetimes
// are tolerated so a half-configured window never 500s the login page.
app.MapGet("/api/system/maintenance", async (ISettingsService settings, CancellationToken ct) =>
{
    var enabled = false;
    string startRaw = string.Empty, endRaw = string.Empty, message = string.Empty;
    try
    {
        enabled = await settings.GetAsync<bool>(SettingKeys.App.MaintenanceEnabled, ct);
        startRaw = await settings.GetAsync<string>(SettingKeys.App.MaintenanceStartUtc, ct);
        endRaw = await settings.GetAsync<string>(SettingKeys.App.MaintenanceEndUtc, ct);
        message = await settings.GetAsync<string>(SettingKeys.App.MaintenanceMessage, ct);
    }
    catch
    {
        // Settings store unreachable — fall through and report inactive.
    }

    DateTimeOffset? start = TryParseUtc(startRaw);
    DateTimeOffset? end = TryParseUtc(endRaw);
    var nowUtc = DateTimeOffset.UtcNow;
    var active = enabled && (end is null || nowUtc <= end);

    return Results.Ok(new
    {
        active,
        startUtc = start?.UtcDateTime,
        endUtc = end?.UtcDateTime,
        message,
    });

    static DateTimeOffset? TryParseUtc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var v))
        {
            return v;
        }
        return null;
    }
})
.WithName("GetSystemMaintenance")
.WithOpenApi();

// Login banner read endpoint. Public — every anonymous auth page (local
// login, M365 callback, future customer-portal login) reads this without
// a session. Distinct from /api/system/maintenance so admins can post a
// notice without flipping the maintenance toggle. Unknown banner-type
// values fall back to "info" so a hand-edited DB row can't break the
// login page. Settings-store failures are swallowed and reported as
// disabled — the login page must keep loading even if settings 500.
app.MapGet("/api/system/login-banner", async (ISettingsService settings, CancellationToken ct) =>
{
    var enabled = false;
    string type = "info", message = string.Empty;
    try
    {
        enabled = await settings.GetAsync<bool>(SettingKeys.App.LoginBannerEnabled, ct);
        type = await settings.GetAsync<string>(SettingKeys.App.LoginBannerType, ct);
        message = await settings.GetAsync<string>(SettingKeys.App.LoginBannerMessage, ct);
    }
    catch
    {
        // Settings store unreachable — report disabled and let the login render.
    }

    var normalizedType = (type ?? "info").Trim().ToLowerInvariant() switch
    {
        "warning" => "warning",
        "error" => "error",
        _ => "info",
    };
    var trimmedMessage = (message ?? string.Empty).Trim();
    var active = enabled && trimmedMessage.Length > 0;

    return Results.Ok(new
    {
        enabled = active,
        type = normalizedType,
        message = trimmedMessage,
    });
})
.WithName("GetLoginBanner")
.WithOpenApi();

// Default theme — public derived-settings endpoint. The login page reads
// this before authentication so anonymous users land on the admin-configured
// default theme on their first ever visit (when no localStorage exists).
// Values: 'steaan' | 'light' | 'dark' (v0.0.108). Unknown / corrupt values
// silently fall back to the factory default so a hand-edited DB row can't
// break paint. Settings-store failures are swallowed and reported as the
// factory default — the login page must always load.
app.MapGet("/api/system/default-theme", async (ISettingsService settings, CancellationToken ct) =>
{
    string? raw = null;
    try
    {
        raw = await settings.GetAsync<string>(SettingKeys.Ui.DefaultTheme, ct);
    }
    catch
    {
        // Fall through with the factory default.
    }
    return Results.Ok(new { theme = UiThemes.NormalizeOrFactory(raw) });
})
.WithName("GetSystemDefaultTheme")
.WithOpenApi();

// Ticket reference prefix — public derived-settings endpoint (v0.0.57). The
// app reads this once at bootstrap so the copy-to-clipboard button produces
// the admin-configured "Ticket#1234" form. Anonymous + cheap (single cached
// settings read); a store failure falls back to the factory default so the
// copy button always has a sane prefix.
app.MapGet("/api/system/ticket-reference-prefix", async (ISettingsService settings, CancellationToken ct) =>
{
    var prefix = TicketReference.DefaultPrefix;
    try
    {
        var raw = await settings.GetAsync<string>(SettingKeys.Tickets.ReferencePrefix, ct);
        if (!string.IsNullOrWhiteSpace(raw)) prefix = raw;
    }
    catch
    {
        // Settings store unreachable — fall through with the factory default.
    }
    return Results.Ok(new { prefix });
})
.WithName("GetTicketReferencePrefix")
.WithOpenApi();

app.MapCspReportEndpoint();
app.MapAuditEndpoints();
app.MapAuthEndpoints();
app.MapMicrosoftAuthEndpoints();
app.MapPortalPublicEndpoints();
app.MapPortalAuthEndpoints();
app.MapPortalTicketEndpoints();
app.MapPortalAdminEndpoints();
app.MapAdminUserEndpoints();
app.MapTaxonomyEndpoints();
app.MapCompanyEndpoints();
app.MapTicketEndpoints();
app.MapTicketProjectEndpoints();
app.MapTicketChecklistEndpoints();
app.MapChecklistTemplateEndpoints();
app.MapTicketExportEndpoints();
app.MapTicketMailEndpoints();
app.MapTicketAttachmentEndpoints();
app.MapTicketClaudeEndpoints();
app.MapSearchEndpoints();
app.MapSettingEndpoints();
app.MapSlaEndpoints();
app.MapTriggerEndpoints();
app.MapTriggerGroupEndpoints();
app.MapIsoEndpoints();
app.MapIntakeFormEndpoints();
app.MapComposeTemplateEndpoints();
app.MapComposeTemplateImageEndpoints();
app.MapTicketTemplateEndpoints();
app.MapTaggingMailboxEndpoints();
app.MapSignatureEndpoints();
app.MapAgentProfileEndpoints();
app.MapSurveyEndpoints();
app.MapSurveyTokenMetadata();
app.MapGraphAdminEndpoints();
app.MapAdsolutEndpoints();
app.MapTelavoxEndpoints();
app.MapZammadEndpoints();
app.MapZammadMappingEndpoints();
app.MapZammadDryRunEndpoints();
app.MapZammadKbImportEndpoints();
app.MapTrmmEndpoints();
app.MapM365Endpoints();
app.MapSophosEndpoints();
app.MapVeeamEndpoints();
app.MapClaudeEndpoints();
app.MapKbChatEndpoints();
app.MapAssetsEndpoints();
app.MapAdminMailDiagnosticsEndpoints();
app.MapMailMailboxEndpoints();
app.MapHealthEndpoints();
app.MapKbConfigEndpoints();
app.MapKbSectionEndpoints();
app.MapKbArticleEndpoints();
app.MapKbAttachmentEndpoints();
app.MapKbPublicEndpoints();
app.MapTimesheetTaskEndpoints();
app.MapTimesheetEntryEndpoints();
app.MapTimesheetManagerEndpoints();
app.MapTicketTimesheetEndpoints();
app.MapTimesheetImportEndpoints();
app.MapReportingApiEndpoints();
app.MapAdsolutTimesheetEndpoints();
app.MapAdsolutCatalogueProductsEndpoints();
app.MapBackofficeTimesheetEndpoints();
app.MapTimesheetCommentEndpoints();
app.MapOrdersEndpoints();
app.MapContractArticlesEndpoints();
app.MapContractsOverviewEndpoints();
app.MapContractM365Endpoints();
app.MapContractReportsEndpoints();
app.MapFeedbackEndpoints();
app.MapFeedbackAttachmentEndpoints();
app.MapFeedbackWorkPointTypeEndpoints();
app.MapViewEndpoints();
app.MapQueueAccessEndpoints();
app.MapViewGroupEndpoints();
app.MapViewAccessEndpoints();
app.MapAgentQueueEndpoints();
app.MapUserEndpoints();
app.MapDashboardEndpoints();
app.MapUserDashboardLayoutEndpoints();
app.MapStatisticsEndpoints();
app.MapRecentTicketsEndpoints();
app.MapActivityEndpoints();
app.MapUserPreferencesEndpoints();
app.MapNotificationEndpoints();
app.MapDevBenchmarkEndpoints(app.Environment);
app.MapHub<TicketPresenceHub>("/hubs/presence");
app.MapHub<UserNotificationHub>("/hubs/notifications");
app.MapHub<IntegrationsHub>("/hubs/integrations");
app.MapHub<TelavoxCallHub>("/hubs/telavox-calls");
app.MapHub<Servicedesk.Api.Activity.ActivityFeedHub>("/hubs/activity");

// Deep-link fallback for the SPA. The regex excludes /api/* and /hubs/* so an
// unknown API route still returns 404 (JSON client) instead of HTML.
app.MapFallbackToFile("{*path:regex(^(?!api/|hubs/).*$)}", "index.html",
    new StaticFileOptions { OnPrepareResponse = indexNoCache });

app.Run();

public partial class Program { }
