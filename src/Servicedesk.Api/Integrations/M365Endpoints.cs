using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.M365;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Microsoft 365 customer-tenant reader (v0.0.77).
/// Admin-only; every route lives under <c>/api/admin/integrations/m365</c>.
/// Configures the install-wide <b>multi-tenant</b> app registration that the
/// MSP owns: tenant id + client id (settings) and the client secret
/// (protected_secrets). The page also surfaces the exact Application
/// permissions and the redirect URI the admin must register in Entra ID, plus
/// a connection test that validates the credentials by requesting a
/// client-credentials token against the MSP's own tenant.
///
/// The per-customer "Connect with M365" admin-consent flow (consent link +
/// callback that stores each customer's tenant id) is a separate, later
/// surface — see ROADMAP → Contracts → Microsoft 365 matching.
public static class M365Endpoints
{
    /// Redirect URI the admin must register on the Entra app. Public (not
    /// admin-gated) because the *customer's* admin — who is not signed in to
    /// Servicedesk — lands here after granting consent; it will be validated
    /// by a server-issued state token when that flow is built. Surfaced here so
    /// the value shown in the UI matches what we will actually use.
    public const string ConsentCallbackPath = "/api/integrations/m365/consent-callback";

    /// Application (app-only) permissions the multi-tenant app needs, consented
    /// by each customer admin. Single-sourced here so the UI never drifts from
    /// what the read pipeline actually requires.
    private static readonly object[] RequiredPermissions =
    {
        new
        {
            name = "User.Read.All",
            type = "Application",
            description = "Enumerate users and their assigned licenses.",
        },
        new
        {
            name = "MailboxSettings.Read",
            type = "Application",
            description = "Read each mailbox's real type (user / shared / room / equipment) via mailboxSettings.userPurpose.",
        },
        new
        {
            name = "Organization.Read.All",
            type = "Application",
            description = "Resolve license SKU GUIDs to readable product names (subscribedSkus).",
        },
    };

    public static IEndpointRouteBuilder MapM365Endpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/m365")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetM365Status").WithOpenApi();

        admin.MapGet("/secret", GetSecretStatus).WithName("GetM365SecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetM365Secret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteM365Secret").WithOpenApi();

        admin.MapPut("/config", SetConfig).WithName("SetM365Config").WithOpenApi();
        admin.MapPut("/enabled", SetEnabled).WithName("SetM365Enabled").WithOpenApi();
        admin.MapPut("/sync-interval", SetSyncInterval).WithName("SetM365SyncInterval").WithOpenApi();

        admin.MapPost("/test-connection", TestConnection).WithName("TestM365Connection").WithOpenApi();

        admin.MapGet("/audit", GetAuditLog).WithName("GetM365AuditLog").WithOpenApi();

        // PUBLIC consent callback — the customer's admin lands here after
        // granting (or declining) admin consent in their own tenant; they are
        // NOT signed in to Servicedesk, so this route is anonymous. Security
        // rests entirely on the single-use, server-time-boxed `state` token
        // minted by the per-company connect endpoint.
        app.MapGet(ConsentCallbackPath, ConsentCallback)
            .AllowAnonymous()
            .WithName("M365ConsentCallback")
            .ExcludeFromDescription();

        return app;
    }

    // ---- consent callback (public) -------------------------------------

    private static async Task<IResult> ConsentCallback(
        HttpContext http,
        [FromQuery] string? state,
        [FromQuery] string? tenant,
        [FromQuery(Name = "admin_consent")] string? adminConsent,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        IM365TenantStore store,
        IM365SyncService sync,
        IIntegrationAuditLogger audit,
        CancellationToken ct)
    {
        // 1. The state token is the whole trust model: consume it atomically.
        if (string.IsNullOrWhiteSpace(state))
            return ConsentResultPage(false, "Missing consent token. Start the connection again from Servicedesk.");

        var pending = await store.ConsumeConsentStateAsync(state, DateTime.UtcNow, ct);
        if (pending is null)
        {
            await audit.LogAsync(new IntegrationAuditEvent(
                Integration: M365EventTypes.Integration,
                EventType: M365EventTypes.ConsentRejected,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "invalid_state",
                Payload: new { reason = "unknown_expired_or_replayed" }), ct);
            return ConsentResultPage(false, "This consent link is invalid, already used or expired. Start the connection again from Servicedesk.");
        }

        // 2. Azure must report a granted consent and a tenant id.
        var granted = string.Equals(adminConsent, "true", StringComparison.OrdinalIgnoreCase);
        if (!granted || string.IsNullOrWhiteSpace(tenant) || !Guid.TryParse(tenant, out _))
        {
            await audit.LogAsync(new IntegrationAuditEvent(
                Integration: M365EventTypes.Integration,
                EventType: M365EventTypes.ConsentRejected,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: error ?? "consent_declined",
                Payload: new { companyId = pending.CompanyId, hasTenant = !string.IsNullOrWhiteSpace(tenant) }), ct);
            var detail = string.IsNullOrWhiteSpace(errorDescription)
                ? "Consent was not granted."
                : errorDescription;
            return ConsentResultPage(false, detail);
        }

        // 3. Persist the link (tenant id is an identifier, not a secret) and
        //    kick an immediate first sync so the data shows up right away.
        await store.UpsertConnectedLinkAsync(pending.CompanyId, tenant.Trim(), pending.InitiatedBy, DateTime.UtcNow, ct);
        await audit.LogAsync(new IntegrationAuditEvent(
            Integration: M365EventTypes.Integration,
            EventType: M365EventTypes.ConsentGranted,
            Outcome: IntegrationAuditOutcome.Ok,
            Payload: new { companyId = pending.CompanyId }), ct);

        var companyId = pending.CompanyId;
        _ = Task.Run(async () =>
        {
            try { await sync.SyncCompanyAsync(companyId, CancellationToken.None); }
            catch { /* best-effort first sync; the worker will retry on its cadence */ }
        }, CancellationToken.None);

        return ConsentResultPage(true, "Microsoft 365 is connected for this customer. You can close this tab and return to Servicedesk.");
    }

    /// Minimal self-contained result page. The customer admin is not a
    /// Servicedesk user, so it links nowhere — it just confirms the outcome.
    private static IResult ConsentResultPage(bool success, string message)
    {
        var title = success ? "Connected" : "Not connected";
        var accent = success ? "#34d399" : "#fb7185";
        var icon = success ? "&#10003;" : "&#10005;";
        var safeMessage = WebUtility.HtmlEncode(message);
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Microsoft 365 — {{title}}</title>
            <style>
              :root { color-scheme: dark; }
              body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
                     background:#0b1020; color:#e5e7eb; font-family:'Segoe UI',system-ui,sans-serif; }
              .card { max-width:30rem; padding:2.5rem; border-radius:1rem; background:rgba(255,255,255,0.04);
                      border:1px solid rgba(255,255,255,0.08); text-align:center; }
              .badge { width:3.25rem; height:3.25rem; border-radius:9999px; display:flex; align-items:center;
                       justify-content:center; margin:0 auto 1.25rem; font-size:1.5rem; color:{{accent}};
                       background:rgba(255,255,255,0.06); border:1px solid {{accent}}33; }
              h1 { font-size:1.25rem; margin:0 0 .5rem; }
              p { color:#9ca3af; line-height:1.5; margin:0; }
            </style></head>
            <body><div class="card">
              <div class="badge">{{icon}}</div>
              <h1>Microsoft 365 — {{title}}</h1>
              <p>{{safeMessage}}</p>
            </div></body></html>
            """;
        return Results.Content(html, "text/html");
    }

    // ---- /status -------------------------------------------------------

    private static async Task<IResult> GetStatus(
        HttpContext http,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.M365.Enabled, ct);
        var tenantId = (await settings.GetAsync<string>(SettingKeys.M365.TenantId, ct) ?? string.Empty).Trim();
        var clientId = (await settings.GetAsync<string>(SettingKeys.M365.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.M365ClientSecret, ct);
        var syncIntervalMinutes = Math.Max(60, await settings.GetAsync<int>(SettingKeys.M365.SyncIntervalMinutes, ct));

        var configured = tenantId.Length > 0 && clientId.Length > 0 && hasSecret;
        var state = !enabled
            ? "disabled"
            : configured ? "ready" : "not-configured";

        var publicBase = await ResolvePublicBaseAsync(http, settings, ct);

        return Results.Ok(new
        {
            state,
            enabled,
            tenantId = tenantId.Length == 0 ? null : tenantId,
            clientId = clientId.Length == 0 ? null : clientId,
            clientSecretConfigured = hasSecret,
            redirectUri = publicBase + ConsentCallbackPath,
            requiredPermissions = RequiredPermissions,
            syncIntervalMinutes,
        });
    }

    // ---- /secret CRUD --------------------------------------------------

    private static async Task<IResult> GetSecretStatus(
        IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.M365ClientSecret, ct) });

    public sealed record SetSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetSecret(
        [FromBody] SetSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "Client secret is required." });

        var trimmed = req.Value.Trim();
        if (trimmed.Length > 4096)
        {
            return Results.BadRequest(new
            {
                error = "invalid_secret",
                message = "Client secret exceeds 4096 characters; check what you pasted (use the secret Value, not the Secret ID).",
            });
        }

        await secrets.SetAsync(ProtectedSecretKeys.M365ClientSecret, trimmed, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: M365EventTypes.SecuritySecretUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.M365ClientSecret,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = true }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecret(
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.M365ClientSecret, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: M365EventTypes.SecuritySecretDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.M365ClientSecret,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }

    // ---- /config (tenant id + client id) -------------------------------

    public sealed record SetConfigRequest(
        [property: Required] string TenantId,
        [property: Required] string ClientId);

    private static async Task<IResult> SetConfig(
        [FromBody] SetConfigRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null)
            return Results.BadRequest(new { error = "missing_body" });

        var tenantId = (req.TenantId ?? string.Empty).Trim();
        var clientId = (req.ClientId ?? string.Empty).Trim();

        if (!IsValidTenantId(tenantId))
        {
            return Results.BadRequest(new
            {
                error = "invalid_tenant_id",
                message = "Tenant ID must be a GUID or a <name>.onmicrosoft.com domain.",
            });
        }
        if (!Guid.TryParse(clientId, out _))
        {
            return Results.BadRequest(new
            {
                error = "invalid_client_id",
                message = "Application (client) ID must be a GUID.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.M365.TenantId, tenantId, actor, role, ct);
        await settings.SetAsync(SettingKeys.M365.ClientId, clientId, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: M365EventTypes.SecurityConfigUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.M365.ClientId,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { tenantId, clientId }), ct);
        return Results.Ok(new { tenantId, clientId });
    }

    // ---- /enabled ------------------------------------------------------

    public sealed record SetEnabledRequest([property: Required] bool Enabled);

    private static async Task<IResult> SetEnabled(
        [FromBody] SetEnabledRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(
            SettingKeys.M365.Enabled,
            req.Enabled ? "true" : "false",
            actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: M365EventTypes.SecurityEnabledChanged,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.M365.Enabled,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { enabled = req.Enabled }), ct);
        return Results.Ok(new { enabled = req.Enabled });
    }

    // ---- /sync-interval ------------------------------------------------

    public sealed record SetSyncIntervalRequest([property: Required] int Minutes);

    private static async Task<IResult> SetSyncInterval(
        [FromBody] SetSyncIntervalRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        // Floor 60 (the worker clamps anyway); a generous ceiling keeps the
        // value sane without being restrictive (1 week).
        var minutes = Math.Clamp(req.Minutes, 60, 10080);

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.M365.SyncIntervalMinutes, minutes.ToString(), actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: M365EventTypes.SecurityConfigUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.M365.SyncIntervalMinutes,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { syncIntervalMinutes = minutes }), ct);
        return Results.Ok(new { syncIntervalMinutes = minutes });
    }

    // ---- /test-connection ----------------------------------------------

    /// Validates the configured app credentials by requesting a
    /// client-credentials token against the MSP's own tenant. A 200 with an
    /// access_token proves the tenant id + client id + secret line up; it does
    /// NOT prove a customer has consented (that is per-customer). Errors from
    /// Azure are surfaced verbatim so a misconfiguration is easy to diagnose.
    private static async Task<IResult> TestConnection(
        HttpContext http,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IHttpClientFactory httpFactory,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var tenantId = (await settings.GetAsync<string>(SettingKeys.M365.TenantId, ct) ?? string.Empty).Trim();
        var clientId = (await settings.GetAsync<string>(SettingKeys.M365.ClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await secrets.GetAsync(ProtectedSecretKeys.M365ClientSecret, ct);

        if (tenantId.Length == 0 || clientId.Length == 0 || string.IsNullOrEmpty(clientSecret))
        {
            return Results.BadRequest(new
            {
                error = "not_configured",
                message = "Set the tenant ID, client ID and client secret before testing.",
            });
        }

        var sw = Stopwatch.StartNew();
        var (actor, role) = ActorContext.Resolve(http);

        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials",
            });

            using var resp = await client.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
                content, ct);
            sw.Stop();

            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                await LogTest(audit, http, actor, role, success: true, ct);
                return Results.Ok(new
                {
                    success = true,
                    latencyMs = (int)sw.ElapsedMilliseconds,
                });
            }

            // Surface the AADSTS error body so the cause is obvious.
            string? errorCode = null;
            string? description = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) errorCode = e.GetString();
                if (doc.RootElement.TryGetProperty("error_description", out var d)) description = d.GetString();
            }
            catch (JsonException) { /* non-JSON upstream error — keep raw below */ }

            await LogTest(audit, http, actor, role, success: false, ct);
            return Results.Json(new
            {
                success = false,
                error = errorCode ?? "token_request_failed",
                message = description ?? body,
                latencyMs = (int)sw.ElapsedMilliseconds,
            }, statusCode: 502);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            await LogTest(audit, http, actor, role, success: false, ct);
            return Results.Json(new
            {
                success = false,
                error = "network_error",
                message = ex.Message,
                latencyMs = (int)sw.ElapsedMilliseconds,
            }, statusCode: 502);
        }
    }

    private static Task LogTest(
        IAuditLogger audit, HttpContext http, string actor, string role, bool success, CancellationToken ct) =>
        audit.LogAsync(new AuditEvent(
            EventType: M365EventTypes.SecurityConnectionTested,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.M365.ClientId,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { success }), ct);

    // ---- /audit --------------------------------------------------------

    private static async Task<IResult> GetAuditLog(
        IIntegrationAuditQuery audit,
        long? cursor,
        int? limit,
        CancellationToken ct)
    {
        var page = await audit.ListAsync(M365EventTypes.Integration, cursor, limit ?? 50, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(e => new
            {
                id = e.Id,
                utc = e.Utc,
                eventType = e.EventType,
                outcome = e.Outcome,
                endpoint = e.Endpoint,
                httpStatus = e.HttpStatus,
                latencyMs = e.LatencyMs,
                actorId = e.ActorId,
                actorRole = e.ActorRole,
                errorCode = e.ErrorCode,
                payload = e.PayloadJson,
            }),
            nextCursor = page.NextCursor,
        });
    }

    // ---- helpers -------------------------------------------------------

    private static bool IsValidTenantId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255) return false;
        if (Guid.TryParse(value, out _)) return true;
        // Accept a domain form (e.g. contoso.onmicrosoft.com or a vanity domain):
        // letters/digits/hyphens in labels separated by dots, at least one dot.
        return Regex.IsMatch(
            value,
            "^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)+[a-zA-Z]{2,}$");
    }

    internal static async Task<string> ResolvePublicBaseAsync(
        HttpContext httpContext,
        ISettingsService settings,
        CancellationToken ct)
    {
        var configured = await settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }
        var request = httpContext.Request;
        return $"{request.Scheme}://{request.Host.Value}";
    }
}
