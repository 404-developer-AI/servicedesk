using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Veeam;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Veeam backup matching integration. Admin-only; every
/// route lives under <c>/api/admin/integrations/veeam</c>. Configures the
/// connection to the MSP's Veeam Service Provider Console (VSPC) REST API:
/// the base URL (unique per install — no default) and API username are
/// settings, the password is a protected_secret. A connection test validates
/// the credentials by requesting an access token (OAuth2 password grant)
/// against the configured console.
///
/// The actual per-company backup read + matching pipeline is a separate, later
/// surface — see ROADMAP → Contracts → Veeam M365-backup matching.
public static class VeeamEndpoints
{
    public static IEndpointRouteBuilder MapVeeamEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/veeam")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetVeeamStatus").WithOpenApi();

        admin.MapGet("/secret", GetSecretStatus).WithName("GetVeeamSecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetVeeamSecret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteVeeamSecret").WithOpenApi();

        admin.MapPut("/config", SetConfig).WithName("SetVeeamConfig").WithOpenApi();
        admin.MapPut("/enabled", SetEnabled).WithName("SetVeeamEnabled").WithOpenApi();
        admin.MapPut("/sync-interval", SetSyncInterval).WithName("SetVeeamSyncInterval").WithOpenApi();

        admin.MapPost("/test-connection", TestConnection).WithName("TestVeeamConnection").WithOpenApi();

        admin.MapGet("/audit", GetAuditLog).WithName("GetVeeamAuditLog").WithOpenApi();

        return app;
    }

    // ---- /status -------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Veeam.Enabled, ct);
        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Veeam.BaseUrl, ct) ?? string.Empty).Trim();
        var username = (await settings.GetAsync<string>(SettingKeys.Veeam.Username, ct) ?? string.Empty).Trim();
        var allowSelfSigned = await settings.GetAsync<bool>(SettingKeys.Veeam.AllowSelfSignedTls, ct);
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.VeeamPassword, ct);
        var syncIntervalMinutes = Math.Max(60, await settings.GetAsync<int>(SettingKeys.Veeam.SyncIntervalMinutes, ct));

        var configured = baseUrl.Length > 0 && username.Length > 0 && hasSecret;
        var state = !enabled
            ? "disabled"
            : configured ? "ready" : "not-configured";

        return Results.Ok(new
        {
            state,
            enabled,
            baseUrl = baseUrl.Length == 0 ? null : baseUrl,
            username = username.Length == 0 ? null : username,
            passwordConfigured = hasSecret,
            syncIntervalMinutes,
            allowSelfSignedTls = allowSelfSigned,
        });
    }

    // ---- /secret CRUD --------------------------------------------------

    private static async Task<IResult> GetSecretStatus(
        IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.VeeamPassword, ct) });

    public sealed record SetVeeamSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetSecret(
        [FromBody] SetVeeamSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "Password is required." });

        var trimmed = req.Value.Trim();
        if (trimmed.Length > 4096)
        {
            return Results.BadRequest(new
            {
                error = "invalid_secret",
                message = "Password exceeds 4096 characters; check what you pasted.",
            });
        }

        await secrets.SetAsync(ProtectedSecretKeys.VeeamPassword, trimmed, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecuritySecretUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.VeeamPassword,
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
        await secrets.DeleteAsync(ProtectedSecretKeys.VeeamPassword, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecuritySecretDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.VeeamPassword,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }

    // ---- /config (base URL + username + TLS toggle) --------------------

    public sealed record SetVeeamConfigRequest(
        [property: Required] string BaseUrl,
        [property: Required] string Username,
        bool AllowSelfSignedTls);

    private static async Task<IResult> SetConfig(
        [FromBody] SetVeeamConfigRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null)
            return Results.BadRequest(new { error = "missing_body" });

        var baseUrl = (req.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var username = (req.Username ?? string.Empty).Trim();

        if (!IsValidBaseUrl(baseUrl))
        {
            return Results.BadRequest(new
            {
                error = "invalid_base_url",
                message = "Base URL must be an absolute http(s) URL including the port, e.g. https://host:1280.",
            });
        }
        if (username.Length == 0)
        {
            return Results.BadRequest(new { error = "invalid_username", message = "Username is required." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Veeam.BaseUrl, baseUrl, actor, role, ct);
        await settings.SetAsync(SettingKeys.Veeam.Username, username, actor, role, ct);
        await settings.SetAsync(SettingKeys.Veeam.AllowSelfSignedTls, req.AllowSelfSignedTls ? "true" : "false", actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecurityConfigUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Veeam.BaseUrl,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { baseUrl, username, allowSelfSignedTls = req.AllowSelfSignedTls }), ct);
        return Results.Ok(new { baseUrl, username, allowSelfSignedTls = req.AllowSelfSignedTls });
    }

    // ---- /enabled ------------------------------------------------------

    public sealed record SetVeeamEnabledRequest([property: Required] bool Enabled);

    private static async Task<IResult> SetEnabled(
        [FromBody] SetVeeamEnabledRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(
            SettingKeys.Veeam.Enabled,
            req.Enabled ? "true" : "false",
            actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecurityEnabledChanged,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Veeam.Enabled,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { enabled = req.Enabled }), ct);
        return Results.Ok(new { enabled = req.Enabled });
    }

    // ---- /sync-interval ------------------------------------------------

    public sealed record SetVeeamSyncIntervalRequest([property: Required] int Minutes);

    private static async Task<IResult> SetSyncInterval(
        [FromBody] SetVeeamSyncIntervalRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        // Floor 60 (the worker clamps anyway); a generous 1-week ceiling.
        var minutes = Math.Clamp(req.Minutes, 60, 10080);

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Veeam.SyncIntervalMinutes, minutes.ToString(), actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecurityConfigUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Veeam.SyncIntervalMinutes,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { syncIntervalMinutes = minutes }), ct);
        return Results.Ok(new { syncIntervalMinutes = minutes });
    }

    // ---- /test-connection ----------------------------------------------

    /// Validates the configured credentials by requesting a VSPC access token
    /// (OAuth2 password grant) against the configured console. A 200 with an
    /// access_token proves the URL + username + password line up. Errors are
    /// surfaced so a misconfiguration is easy to diagnose. When the
    /// AllowSelfSignedTls setting is on, certificate validation is bypassed for
    /// this call only — VSPC appliances commonly ship a self-signed cert.
    private static async Task<IResult> TestConnection(
        HttpContext http,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Veeam.BaseUrl, ct) ?? string.Empty).Trim().TrimEnd('/');
        var username = (await settings.GetAsync<string>(SettingKeys.Veeam.Username, ct) ?? string.Empty).Trim();
        var password = await secrets.GetAsync(ProtectedSecretKeys.VeeamPassword, ct);
        var allowSelfSigned = await settings.GetAsync<bool>(SettingKeys.Veeam.AllowSelfSignedTls, ct);

        if (baseUrl.Length == 0 || username.Length == 0 || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new
            {
                error = "not_configured",
                message = "Set the base URL, username and password before testing.",
            });
        }
        if (!IsValidBaseUrl(baseUrl))
        {
            return Results.BadRequest(new { error = "invalid_base_url", message = "Base URL is not a valid http(s) URL." });
        }

        var sw = Stopwatch.StartNew();
        var (actor, role) = ActorContext.Resolve(http);

        using var handler = new HttpClientHandler();
        if (allowSelfSigned)
        {
            // Explicit, audited opt-in (Veeam.AllowSelfSignedTls). VSPC appliances
            // ship self-signed certs; never bypass validation without this flag.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = password,
            });

            using var resp = await client.PostAsync($"{baseUrl}/api/v3/token", content, ct);
            sw.Stop();

            var body = await resp.Content.ReadAsStringAsync(ct);

            var hasToken = false;
            string? errorCode = null;
            string? description = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String)
                    hasToken = !string.IsNullOrEmpty(t.GetString());
                if (doc.RootElement.TryGetProperty("error", out var e)) errorCode = e.GetString();
                if (doc.RootElement.TryGetProperty("message", out var m)) description = m.GetString();
                else if (doc.RootElement.TryGetProperty("error_description", out var d)) description = d.GetString();
            }
            catch (JsonException) { /* non-JSON upstream body — keep raw below */ }

            if (resp.IsSuccessStatusCode && hasToken)
            {
                await LogTest(audit, http, actor, role, success: true, ct);
                return Results.Ok(new { success = true, latencyMs = (int)sw.ElapsedMilliseconds });
            }

            await LogTest(audit, http, actor, role, success: false, ct);
            return Results.Json(new
            {
                success = false,
                error = errorCode ?? "token_request_failed",
                message = description ?? (body.Length > 0 ? body : $"HTTP {(int)resp.StatusCode}"),
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
            EventType: VeeamEventTypes.SecurityConnectionTested,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Veeam.BaseUrl,
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
        var page = await audit.ListAsync(VeeamEventTypes.Integration, cursor, limit ?? 50, ct);
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

    private static bool IsValidBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
