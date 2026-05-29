using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Trmm;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Tactical RMM integration (v0.0.52). Admin-only;
/// every route lives under <c>/api/admin/integrations/trmm</c>. Settings
/// CRUD (base URL, API key, enable toggle, sync interval), a connection
/// test, a manual <c>Sync now</c> trigger, the client-mapping editor and
/// an integration-audit reader.
public static class TrmmEndpoints
{
    public static IEndpointRouteBuilder MapTrmmEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/trmm")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetTrmmStatus").WithOpenApi();

        admin.MapGet("/secret", GetSecretStatus).WithName("GetTrmmSecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetTrmmSecret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteTrmmSecret").WithOpenApi();

        admin.MapPut("/base-url", SetBaseUrl).WithName("SetTrmmBaseUrl").WithOpenApi();
        admin.MapPut("/enabled", SetEnabled).WithName("SetTrmmEnabled").WithOpenApi();
        admin.MapPut("/sync-interval", SetSyncInterval).WithName("SetTrmmSyncInterval").WithOpenApi();

        admin.MapPost("/test-connection", TestConnection).WithName("TestTrmmConnection").WithOpenApi();
        admin.MapPost("/sync", TriggerSync).WithName("TriggerTrmmSync").WithOpenApi();

        admin.MapGet("/client-mappings", ListClientMappings).WithName("ListTrmmClientMappings").WithOpenApi();
        admin.MapPut("/client-mappings/{trmmClientId:long}", SetClientMapping).WithName("SetTrmmClientMapping").WithOpenApi();

        admin.MapGet("/audit", GetAuditLog).WithName("GetTrmmAuditLog").WithOpenApi();

        return app;
    }

    // ---- /status -------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAssetRepository assets,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct);
        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Trmm.BaseUrl, ct) ?? string.Empty).Trim();
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Trmm.SyncIntervalMinutes, ct);
        var hasApiKey = await secrets.HasAsync(ProtectedSecretKeys.TrmmApiKey, ct);
        var syncState = await assets.GetSyncStateAsync(ct);

        var state = !enabled
            ? "disabled"
            : (!hasApiKey || baseUrl.Length == 0) ? "not-configured" : "ready";

        return Results.Ok(new
        {
            state,
            enabled,
            apiKeyConfigured = hasApiKey,
            baseUrl = baseUrl.Length == 0 ? null : baseUrl,
            syncIntervalMinutes = intervalMinutes,
            lastSyncUtc = syncState.LastSyncUtc,
            lastStatus = syncState.LastStatus,
            lastError = syncState.LastError,
        });
    }

    // ---- /secret CRUD --------------------------------------------------

    private static async Task<IResult> GetSecretStatus(
        IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.TrmmApiKey, ct) });

    public sealed record SetSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetSecret(
        [FromBody] SetSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "API key is required." });

        var trimmed = req.Value.Trim();
        if (trimmed.Length > 4096)
        {
            return Results.BadRequest(new
            {
                error = "invalid_key",
                message = "API key exceeds 4096 characters; check what you pasted.",
            });
        }

        await secrets.SetAsync(ProtectedSecretKeys.TrmmApiKey, trimmed, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: TrmmEventTypes.SecurityApiKeyUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.TrmmApiKey,
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
        await secrets.DeleteAsync(ProtectedSecretKeys.TrmmApiKey, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: TrmmEventTypes.SecurityApiKeyDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.TrmmApiKey,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }

    // ---- /base-url -----------------------------------------------------

    public sealed record SetBaseUrlRequest([property: Required] string BaseUrl);

    private static async Task<IResult> SetBaseUrl(
        [FromBody] SetBaseUrlRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.BaseUrl))
        {
            return Results.BadRequest(new
            {
                error = "missing_base_url",
                message = "Base URL is required (e.g. https://api.trmm.example.com).",
            });
        }

        var trimmed = req.BaseUrl.Trim().TrimEnd('/');
        if (!TryValidateBaseUrl(trimmed, out var validationError))
        {
            return Results.BadRequest(new { error = "invalid_base_url", message = validationError });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Trmm.BaseUrl, trimmed, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: TrmmEventTypes.SecurityBaseUrlUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Trmm.BaseUrl,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { baseUrl = trimmed }), ct);
        return Results.Ok(new { baseUrl = trimmed });
    }

    private static bool TryValidateBaseUrl(string raw, out string error)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            error = "Could not parse as an absolute URL. Use the full https://… form.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "Scheme must be http or https.";
            return false;
        }
        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            error = "URL is missing a host.";
            return false;
        }
        if (host.Length > 255)
        {
            error = "Host is too long.";
            return false;
        }
        var isLocalhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1" || host == "::1";
        if (uri.Scheme == Uri.UriSchemeHttp && !isLocalhost)
        {
            error = "Only https is allowed for non-localhost hosts — http would route the API key over plaintext.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            error = "Base URL must not include a path (the client appends /clients/, /sites/, /agents/ itself).";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Base URL must not include a query string or fragment.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    // ---- /enabled + /sync-interval ------------------------------------

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
            SettingKeys.Trmm.Enabled,
            req.Enabled ? "true" : "false",
            actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: TrmmEventTypes.SecurityEnabledChanged,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Trmm.Enabled,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { enabled = req.Enabled }), ct);
        return Results.Ok(new { enabled = req.Enabled });
    }

    public sealed record SetSyncIntervalRequest([property: Required] int Minutes);

    private static async Task<IResult> SetSyncInterval(
        [FromBody] SetSyncIntervalRequest req,
        HttpContext http,
        ISettingsService settings,
        CancellationToken ct)
    {
        if (req is null || req.Minutes < 1 || req.Minutes > 1440)
        {
            return Results.BadRequest(new
            {
                error = "invalid_interval",
                message = "Sync interval must be between 1 and 1440 minutes.",
            });
        }
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(
            SettingKeys.Trmm.SyncIntervalMinutes,
            req.Minutes.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            actor, role, ct);
        return Results.Ok(new { syncIntervalMinutes = req.Minutes });
    }

    // ---- /test-connection ---------------------------------------------

    private static async Task<IResult> TestConnection(
        ITrmmApiClient client,
        CancellationToken ct)
    {
        var result = await client.TestConnectionAsync(ct);
        if (result.Success)
        {
            return Results.Ok(new
            {
                success = true,
                clientCount = result.ClientCount,
                latencyMs = result.LatencyMs,
            });
        }
        return Results.Json(new
        {
            error = "upstream_error",
            errorCode = result.ErrorCode,
            message = result.ErrorMessage,
            latencyMs = result.LatencyMs,
        }, statusCode: 502);
    }

    // ---- /sync (manual trigger) ---------------------------------------

    private static async Task<IResult> TriggerSync(
        HttpContext http,
        ITrmmSyncService sync,
        ITrmmSyncNotifier notifier,
        IAuditLogger audit,
        ISettingsService settings,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct);
        if (!enabled)
        {
            return Results.Json(new
            {
                error = "integration_disabled",
                message = "Tactical RMM integration is disabled. Toggle it on first.",
            }, statusCode: 409);
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: TrmmEventTypes.SecuritySyncTriggered,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Trmm.Enabled,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { trigger = "manual" }), ct);

        var outcome = await sync.RunOnceAsync("manual", ct);
        if (outcome.Success)
        {
            await notifier.NotifyAssetsChangedAsync(outcome, ct);
        }
        return Results.Ok(new
        {
            success = outcome.Success,
            clients = outcome.Clients,
            sites = outcome.Sites,
            agents = outcome.Agents,
            autoLinkedCompanies = outcome.AutoLinkedCompanies,
            latencyMs = outcome.LatencyMs,
            errorCode = outcome.ErrorCode,
            errorMessage = outcome.ErrorMessage,
        });
    }

    // ---- /client-mappings ---------------------------------------------

    private static async Task<IResult> ListClientMappings(
        IAssetRepository repo, CancellationToken ct)
    {
        var rows = await repo.ListClientMappingsAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(r => new
            {
                trmmClientId = r.TrmmClientId,
                name = r.Name,
                code = r.Code,
                autoMatched = r.AutoMatched,
                companyId = r.CompanyId,
                companyName = r.CompanyName,
                companyCode = r.CompanyCode,
                agentCount = r.AgentCount,
            }),
        });
    }

    public sealed record SetClientMappingRequest(Guid? CompanyId, bool ClearOverride);

    private static async Task<IResult> SetClientMapping(
        long trmmClientId,
        [FromBody] SetClientMappingRequest req,
        HttpContext http,
        IAssetRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        if (!req.ClearOverride && req.CompanyId is null)
        {
            return Results.BadRequest(new
            {
                error = "missing_company",
                message = "Either set a companyId or send clearOverride=true to revert to auto-match.",
            });
        }

        var ok = await repo.SetClientMappingAsync(trmmClientId, req.CompanyId, req.ClearOverride, ct);
        if (!ok) return Results.NotFound();

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: TrmmEventTypes.SecurityClientMappingSet,
            Actor: actor,
            ActorRole: role,
            Target: trmmClientId.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new
            {
                trmmClientId,
                companyId = req.CompanyId,
                clearOverride = req.ClearOverride,
            }), ct);
        return Results.NoContent();
    }

    // ---- /audit --------------------------------------------------------

    private static async Task<IResult> GetAuditLog(
        IIntegrationAuditQuery audit,
        long? cursor,
        int? limit,
        CancellationToken ct)
    {
        var page = await audit.ListAsync(TrmmEventTypes.Integration, cursor, limit ?? 50, ct);
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
}
