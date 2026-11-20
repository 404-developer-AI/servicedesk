using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.TdSynnex;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// Admin surface of the TD SYNNEX connector (Order Management, v0.1.59) under
/// <c>/api/admin/integrations/tdsynnex</c>: client id + secret (write-only,
/// protected_secrets), base/token URL (https on a tdsynnex.com host only),
/// cadence, the enable toggle, a credential test, "Sync now" and the
/// connector's integration_audit log. Every change is audited.
public static class TdSynnexEndpoints
{
    public static IEndpointRouteBuilder MapTdSynnexEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/tdsynnex")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetTdSynnexStatus");
        admin.MapPut("/credentials", SetCredentials).WithName("SetTdSynnexCredentials");
        admin.MapDelete("/credentials", DeleteCredentials).WithName("DeleteTdSynnexCredentials");
        admin.MapPut("/enabled", SetEnabled).WithName("SetTdSynnexEnabled");
        admin.MapPut("/config", SetConfig).WithName("SetTdSynnexConfig");
        admin.MapPost("/test-connection", TestConnection).WithName("TestTdSynnexConnection");
        admin.MapPost("/sync", SyncNow).WithName("SyncTdSynnexNow");
        admin.MapGet("/audit", GetAuditLog).WithName("GetTdSynnexAuditLog");
        return app;
    }

    private static async Task<IResult> GetStatus(
        ISettingsService settings, IProtectedSecretStore secrets, ITdSynnexOrderRepository repo, CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.TdSynnex.Enabled, ct);
        var baseUrl = (await settings.GetAsync<string>(SettingKeys.TdSynnex.BaseUrl, ct) ?? string.Empty).Trim();
        var tokenUrl = (await settings.GetAsync<string>(SettingKeys.TdSynnex.TokenUrl, ct) ?? string.Empty).Trim();
        var hasClientId = await secrets.HasAsync(ProtectedSecretKeys.TdSynnexClientId, ct);
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.TdSynnexClientSecret, ct);
        var sync = await repo.GetSyncStateAsync(ct);

        var configured = hasClientId && hasSecret && baseUrl.Length > 0;
        return Results.Ok(new
        {
            state = !enabled ? "disabled" : configured ? "ready" : "not-configured",
            enabled,
            baseUrl,
            tokenUrl,
            clientIdConfigured = hasClientId,
            clientSecretConfigured = hasSecret,
            syncIntervalMinutes = Math.Max(10, await settings.GetAsync<int>(SettingKeys.TdSynnex.SyncIntervalMinutes, ct)),
            initialSyncDays = Math.Clamp(await settings.GetAsync<int>(SettingKeys.TdSynnex.InitialSyncDays, ct), 1, 730),
            activeRefreshMinutes = Math.Max(60, await settings.GetAsync<int>(SettingKeys.TdSynnex.ActiveRefreshMinutes, ct)),
            lastCheckedUtc = sync?.LastCheckedUtc,
            lastSuccessUtc = sync?.LastSuccessUtc,
            lastActiveRefreshUtc = sync?.LastActiveRefreshUtc,
            lastStatus = sync?.LastStatus,
            lastError = sync?.LastError,
            lastSeen = sync?.LastSeen ?? 0,
            lastChanged = sync?.LastChanged ?? 0,
            orderCount = sync?.OrderCount ?? 0,
        });
    }

    public sealed record SetCredentialsRequest(string? ClientId, string? ClientSecret);

    private static async Task<IResult> SetCredentials(
        [FromBody] SetCredentialsRequest req, HttpContext http, IProtectedSecretStore secrets,
        ITdSynnexApiClient client, IAuditLogger audit, CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        var clientId = req.ClientId?.Trim();
        var clientSecret = req.ClientSecret?.Trim();
        if (string.IsNullOrEmpty(clientId) && string.IsNullOrEmpty(clientSecret))
            return Results.BadRequest(new { error = "missing_value", message = "Provide a client ID and/or client secret." });
        if ((clientId?.Length ?? 0) > 1024 || (clientSecret?.Length ?? 0) > 4096)
            return Results.BadRequest(new { error = "invalid_value", message = "Credential value is too long; check what you pasted." });

        var updated = new List<string>();
        if (!string.IsNullOrEmpty(clientId))
        {
            await secrets.SetAsync(ProtectedSecretKeys.TdSynnexClientId, clientId, ct);
            updated.Add("clientId");
        }
        if (!string.IsNullOrEmpty(clientSecret))
        {
            await secrets.SetAsync(ProtectedSecretKeys.TdSynnexClientSecret, clientSecret, ct);
            updated.Add("clientSecret");
        }
        client.InvalidateToken();

        await LogAsync(http, audit, TdSynnexEventTypes.SecuritySecretUpdated,
            ProtectedSecretKeys.TdSynnexClientSecret, new { updated }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteCredentials(
        HttpContext http, IProtectedSecretStore secrets, ITdSynnexApiClient client, IAuditLogger audit, CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.TdSynnexClientId, ct);
        await secrets.DeleteAsync(ProtectedSecretKeys.TdSynnexClientSecret, ct);
        client.InvalidateToken();
        await LogAsync(http, audit, TdSynnexEventTypes.SecuritySecretDeleted,
            ProtectedSecretKeys.TdSynnexClientSecret, new { configured = false }, ct);
        return Results.NoContent();
    }

    public sealed record SetEnabledRequest([property: Required] bool Enabled);

    private static async Task<IResult> SetEnabled(
        [FromBody] SetEnabledRequest req, HttpContext http, ISettingsService settings, IAuditLogger audit, CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.TdSynnex.Enabled, req.Enabled ? "true" : "false", actor, role, ct);
        await LogAsync(http, audit, TdSynnexEventTypes.SecurityEnabledChanged,
            SettingKeys.TdSynnex.Enabled, new { enabled = req.Enabled }, ct);
        return Results.Ok(new { enabled = req.Enabled });
    }

    public sealed record SetConfigRequest(
        string? BaseUrl, string? TokenUrl, int? SyncIntervalMinutes, int? InitialSyncDays, int? ActiveRefreshMinutes);

    private static async Task<IResult> SetConfig(
        [FromBody] SetConfigRequest req, HttpContext http, ISettingsService settings,
        ITdSynnexApiClient client, IAuditLogger audit, CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });

        var baseUrl = req.BaseUrl?.Trim().TrimEnd('/');
        var tokenUrl = req.TokenUrl?.Trim();
        if (baseUrl is { Length: > 0 } && !TdSynnexEndpointPolicy.IsAllowed(baseUrl))
            return Results.BadRequest(new { error = "invalid_base_url", message = "The base URL must be an https URL on a tdsynnex.com host, without query string." });
        if (tokenUrl is { Length: > 0 } && !TdSynnexEndpointPolicy.IsAllowed(tokenUrl))
            return Results.BadRequest(new { error = "invalid_token_url", message = "The token URL must be an https URL on a tdsynnex.com host, without query string." });

        var (actor, role) = ActorContext.Resolve(http);
        if (baseUrl is not null) await settings.SetAsync(SettingKeys.TdSynnex.BaseUrl, baseUrl, actor, role, ct);
        if (tokenUrl is not null) await settings.SetAsync(SettingKeys.TdSynnex.TokenUrl, tokenUrl, actor, role, ct);
        if (req.SyncIntervalMinutes is { } si)
            await settings.SetAsync(SettingKeys.TdSynnex.SyncIntervalMinutes, Math.Clamp(si, 10, 1440).ToString(), actor, role, ct);
        if (req.InitialSyncDays is { } d)
            await settings.SetAsync(SettingKeys.TdSynnex.InitialSyncDays, Math.Clamp(d, 1, 730).ToString(), actor, role, ct);
        if (req.ActiveRefreshMinutes is { } ar)
            await settings.SetAsync(SettingKeys.TdSynnex.ActiveRefreshMinutes, Math.Clamp(ar, 60, 10080).ToString(), actor, role, ct);
        if (baseUrl is not null || tokenUrl is not null) client.InvalidateToken();

        await LogAsync(http, audit, TdSynnexEventTypes.SecurityConfigUpdated, "TdSynnex", new
        {
            baseUrl,
            tokenUrl,
            req.SyncIntervalMinutes,
            req.InitialSyncDays,
            req.ActiveRefreshMinutes,
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestConnection(
        HttpContext http, ITdSynnexApiClient client, IAuditLogger audit, CancellationToken ct)
    {
        var result = await client.ProbeAsync(ct);
        await LogAsync(http, audit, TdSynnexEventTypes.SecurityConnectionTested,
            ProtectedSecretKeys.TdSynnexClientId, new { success = result.Success, listKey = result.ListKey }, ct);
        if (result.Success)
            return Results.Ok(new { success = true, totalResults = result.TotalResults, listKey = result.ListKey });
        return Results.Json(new
        {
            success = false,
            error = "test_failed",
            message = result.Error ?? "TD SYNNEX connection test failed.",
        }, statusCode: 502);
    }

    /// Runs on the application lifetime, not the request: a long first sync
    /// must not be cut off when the proxy times the request out.
    private static async Task<IResult> SyncNow(ITdSynnexSyncService sync, IHostApplicationLifetime lifetime)
    {
        var o = await sync.SyncAsync(forceActiveRefresh: true, lifetime.ApplicationStopping);
        return Results.Ok(new { success = o.Success, status = o.Status, error = o.Error, seen = o.Seen, upserted = o.Upserted, changed = o.Changed });
    }

    private static async Task<IResult> GetAuditLog(IIntegrationAuditQuery audit, long? cursor, int? limit, CancellationToken ct)
    {
        var page = await audit.ListAsync(TdSynnexEventTypes.Integration, cursor, Math.Clamp(limit ?? 50, 1, 200), ct);
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

    private static Task LogAsync(HttpContext http, IAuditLogger audit, string eventType, string target, object payload, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);
    }
}
