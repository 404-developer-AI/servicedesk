using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
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

        admin.MapPost("/sync", SyncNow).WithName("SyncVeeamNow").WithOpenApi();
        admin.MapGet("/companies", GetCompanies).WithName("GetVeeamCompanies").WithOpenApi();

        admin.MapGet("/vspc-companies", GetVspcCompanies).WithName("GetVeeamVspcCompanies").WithOpenApi();
        admin.MapGet("/links", GetCompanyLinks).WithName("GetVeeamCompanyLinks").WithOpenApi();
        admin.MapGet("/linkable-companies", GetLinkableCompanies).WithName("GetVeeamLinkableCompanies").WithOpenApi();
        admin.MapPost("/links", AddCompanyLink).WithName("AddVeeamCompanyLink").WithOpenApi();
        admin.MapDelete("/links/{parentCompanyId:guid}/{vspcCompanyUid}", RemoveCompanyLink).WithName("RemoveVeeamCompanyLink").WithOpenApi();

        admin.MapGet("/audit", GetAuditLog).WithName("GetVeeamAuditLog").WithOpenApi();

        return app;
    }

    // ---- /status -------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IVeeamStore store,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Veeam.Enabled, ct);
        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Veeam.BaseUrl, ct) ?? string.Empty).Trim();
        var username = (await settings.GetAsync<string>(SettingKeys.Veeam.Username, ct) ?? string.Empty).Trim();
        var allowSelfSigned = await settings.GetAsync<bool>(SettingKeys.Veeam.AllowSelfSignedTls, ct);
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.VeeamPassword, ct);
        var syncIntervalMinutes = Math.Max(60, await settings.GetAsync<int>(SettingKeys.Veeam.SyncIntervalMinutes, ct));
        var sync = await store.GetSyncStateAsync(ct);

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
            lastCheckedUtc = sync?.LastCheckedUtc,
            lastChangedUtc = sync?.LastChangedUtc,
            lastStatus = sync?.LastStatus,
            lastError = sync?.LastError,
            companyCount = sync?.CompanyCount ?? 0,
            objectCount = sync?.ObjectCount ?? 0,
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
        IVeeamApiClient client,
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

        var conn = new VeeamConnection(baseUrl, username, password, allowSelfSigned);
        var probe = await client.ProbeAsync(conn, ct);
        sw.Stop();

        if (probe.Success)
        {
            await LogTest(audit, http, actor, role, success: true, ct);
            return Results.Ok(new
            {
                success = true,
                latencyMs = (int)sw.ElapsedMilliseconds,
                companyCount = probe.CompanyCount,
            });
        }

        await LogTest(audit, http, actor, role, success: false, ct);
        return Results.Json(new
        {
            success = false,
            error = "token_request_failed",
            message = probe.Error ?? "The Veeam console rejected the connection.",
            latencyMs = (int)sw.ElapsedMilliseconds,
        }, statusCode: 502);
    }

    // ---- /sync ---------------------------------------------------------

    private static async Task<IResult> SyncNow(IVeeamSyncService sync, CancellationToken ct)
    {
        var outcome = await sync.SyncAsync(ct);
        return Results.Ok(new
        {
            success = outcome.Success,
            status = outcome.Status,
            error = outcome.Error,
            companyCount = outcome.CompanyCount,
            objectCount = outcome.ObjectCount,
            changed = outcome.Changed,
        });
    }

    // ---- /companies ----------------------------------------------------

    private static async Task<IResult> GetCompanies(IVeeamStore store, CancellationToken ct)
    {
        var rows = await store.GetCompaniesAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(c => new
            {
                companyId = c.CompanyId,
                companyCode = c.CompanyCode,
                companyName = c.CompanyName,
                vspcCompanyName = c.VspcCompanyName,
                objectCount = c.ObjectCount,
                lastSyncedUtc = c.LastSyncedUtc,
            }),
        });
    }

    // ---- VSPC company rollup links -------------------------------------

    private static async Task<IResult> GetVspcCompanies(IVeeamStore store, CancellationToken ct)
    {
        var rows = await store.GetVspcCompaniesAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(v => new
            {
                vspcCompanyUid = v.VspcCompanyUid,
                code = v.Code,
                name = v.Name,
                matchedCompanyId = v.MatchedCompanyId,
                matchedCompanyName = v.MatchedCompanyName,
            }),
        });
    }

    private static async Task<IResult> GetCompanyLinks(IVeeamStore store, CancellationToken ct)
    {
        var rows = await store.GetCompanyLinksAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(l => new
            {
                parentCompanyId = l.ParentCompanyId,
                parentCompanyName = l.ParentCompanyName,
                vspcCompanyUid = l.VspcCompanyUid,
                vspcCompanyName = l.VspcCompanyName,
                vspcCompanyCode = l.VspcCompanyCode,
            }),
        });
    }

    private static async Task<IResult> GetLinkableCompanies(IVeeamStore store, CancellationToken ct)
    {
        var rows = await store.GetLinkableCompaniesAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(c => new { companyId = c.CompanyId, name = c.Name, code = c.Code }),
        });
    }

    public sealed record AddCompanyLinkRequest(
        [property: Required] Guid ParentCompanyId,
        [property: Required] string VspcCompanyUid);

    private static async Task<IResult> AddCompanyLink(
        [FromBody] AddCompanyLinkRequest req,
        HttpContext http,
        IVeeamStore store,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || req.ParentCompanyId == Guid.Empty || string.IsNullOrWhiteSpace(req.VspcCompanyUid))
            return Results.BadRequest(new { error = "missing_value", message = "A company and a VSPC company are required." });

        var ok = await store.AddCompanyLinkAsync(
            req.ParentCompanyId, req.VspcCompanyUid.Trim(), ActorContext.GetUserId(http), ct);
        if (!ok)
            return Results.BadRequest(new
            {
                error = "invalid_link",
                message = "The company is not connected with Microsoft 365, or the VSPC company is not in the last sync.",
            });

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecurityCompanyLinked,
            Actor: actor,
            ActorRole: role,
            Target: req.VspcCompanyUid,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { parentCompanyId = req.ParentCompanyId, vspcCompanyUid = req.VspcCompanyUid }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveCompanyLink(
        Guid parentCompanyId,
        string vspcCompanyUid,
        HttpContext http,
        IVeeamStore store,
        IAuditLogger audit,
        CancellationToken ct)
    {
        await store.RemoveCompanyLinkAsync(parentCompanyId, vspcCompanyUid, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: VeeamEventTypes.SecurityCompanyUnlinked,
            Actor: actor,
            ActorRole: role,
            Target: vspcCompanyUid,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { parentCompanyId, vspcCompanyUid }), ct);
        return Results.NoContent();
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
