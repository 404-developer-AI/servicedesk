using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Sophos;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Sophos Central spam-filter connector (v0.0.78).
/// Admin-only; every route lives under <c>/api/admin/integrations/sophos</c>.
/// Configures the install-wide partner API credentials (client id + secret,
/// both in protected_secrets), the sync cadence and the enable toggle; exposes a
/// credential test, a manual sync trigger, the synced tenant list (with the
/// resolved company match) and the connector's integration_audit log.
public static class SophosEndpoints
{
    public static IEndpointRouteBuilder MapSophosEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/sophos")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetSophosStatus").WithOpenApi();
        admin.MapPut("/credentials", SetCredentials).WithName("SetSophosCredentials").WithOpenApi();
        admin.MapDelete("/credentials", DeleteCredentials).WithName("DeleteSophosCredentials").WithOpenApi();
        admin.MapPut("/enabled", SetEnabled).WithName("SetSophosEnabled").WithOpenApi();
        admin.MapPut("/sync-interval", SetSyncInterval).WithName("SetSophosSyncInterval").WithOpenApi();
        admin.MapPost("/test-connection", TestConnection).WithName("TestSophosConnection").WithOpenApi();
        admin.MapPost("/sync", SyncNow).WithName("SyncSophosNow").WithOpenApi();
        admin.MapGet("/tenants", GetTenants).WithName("GetSophosTenants").WithOpenApi();
        admin.MapGet("/audit", GetAuditLog).WithName("GetSophosAuditLog").WithOpenApi();

        return app;
    }

    // ---- /status -------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        ISophosStore store,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Sophos.Enabled, ct);
        var hasClientId = await secrets.HasAsync(ProtectedSecretKeys.SophosClientId, ct);
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.SophosClientSecret, ct);
        var syncIntervalMinutes = Math.Max(60, await settings.GetAsync<int>(SettingKeys.Sophos.SyncIntervalMinutes, ct));
        var sync = await store.GetSyncStateAsync(ct);

        var configured = hasClientId && hasSecret;
        var state = !enabled ? "disabled" : configured ? "ready" : "not-configured";

        return Results.Ok(new
        {
            state,
            enabled,
            clientIdConfigured = hasClientId,
            clientSecretConfigured = hasSecret,
            syncIntervalMinutes,
            lastCheckedUtc = sync?.LastCheckedUtc,
            lastChangedUtc = sync?.LastChangedUtc,
            lastStatus = sync?.LastStatus,
            lastError = sync?.LastError,
            tenantCount = sync?.TenantCount ?? 0,
            mailboxCount = sync?.MailboxCount ?? 0,
        });
    }

    // ---- /credentials --------------------------------------------------

    public sealed record SetCredentialsRequest(string? ClientId, string? ClientSecret);

    private static async Task<IResult> SetCredentials(
        [FromBody] SetCredentialsRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        ISophosApiClient client,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "missing_body" });

        var clientId = req.ClientId?.Trim();
        var clientSecret = req.ClientSecret?.Trim();
        if (string.IsNullOrEmpty(clientId) && string.IsNullOrEmpty(clientSecret))
            return Results.BadRequest(new { error = "missing_value", message = "Provide a client ID and/or client secret." });

        if ((clientId?.Length ?? 0) > 4096 || (clientSecret?.Length ?? 0) > 4096)
            return Results.BadRequest(new { error = "invalid_value", message = "Credential value is too long; check what you pasted." });

        var (actor, role) = ActorContext.Resolve(http);
        var updated = new List<string>();
        if (!string.IsNullOrEmpty(clientId))
        {
            await secrets.SetAsync(ProtectedSecretKeys.SophosClientId, clientId, ct);
            updated.Add("clientId");
        }
        if (!string.IsNullOrEmpty(clientSecret))
        {
            await secrets.SetAsync(ProtectedSecretKeys.SophosClientSecret, clientSecret, ct);
            updated.Add("clientSecret");
        }

        // A new credential invalidates the cached partner token immediately.
        client.InvalidateToken();

        await audit.LogAsync(new AuditEvent(
            EventType: SophosEventTypes.SecuritySecretUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.SophosClientSecret,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { updated }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteCredentials(
        HttpContext http,
        IProtectedSecretStore secrets,
        ISophosApiClient client,
        IAuditLogger audit,
        CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.SophosClientId, ct);
        await secrets.DeleteAsync(ProtectedSecretKeys.SophosClientSecret, ct);
        client.InvalidateToken();

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: SophosEventTypes.SecuritySecretDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.SophosClientSecret,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
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
        await settings.SetAsync(SettingKeys.Sophos.Enabled, req.Enabled ? "true" : "false", actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: SophosEventTypes.SecurityEnabledChanged,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Sophos.Enabled,
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
        var minutes = Math.Clamp(req.Minutes, 60, 10080);
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Sophos.SyncIntervalMinutes, minutes.ToString(), actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: SophosEventTypes.SecurityConfigUpdated,
            Actor: actor,
            ActorRole: role,
            Target: SettingKeys.Sophos.SyncIntervalMinutes,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { syncIntervalMinutes = minutes }), ct);
        return Results.Ok(new { syncIntervalMinutes = minutes });
    }

    // ---- /test-connection ----------------------------------------------

    /// Validate the saved credentials without storing anything: token →
    /// whoami → tenant count. Surfaces the upstream error verbatim so a
    /// misconfiguration is easy to diagnose.
    private static async Task<IResult> TestConnection(
        HttpContext http,
        IProtectedSecretStore secrets,
        ISophosApiClient client,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var clientId = (await secrets.GetAsync(ProtectedSecretKeys.SophosClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await secrets.GetAsync(ProtectedSecretKeys.SophosClientSecret, ct);
        if (clientId.Length == 0 || string.IsNullOrEmpty(clientSecret))
            return Results.BadRequest(new { error = "not_configured", message = "Set the client ID and client secret before testing." });

        var result = await client.ProbeAsync(clientId, clientSecret, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: SophosEventTypes.SecurityConnectionTested,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.SophosClientId,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { success = result.Success }), ct);

        if (result.Success)
            return Results.Ok(new { success = true, idType = result.IdType, tenantCount = result.TenantCount });

        return Results.Json(new
        {
            success = false,
            error = "test_failed",
            message = result.Error ?? "Sophos credential test failed.",
        }, statusCode: 502);
    }

    // ---- /sync ---------------------------------------------------------

    private static async Task<IResult> SyncNow(
        ISophosSyncService sync,
        CancellationToken ct)
    {
        var outcome = await sync.SyncAsync(ct);
        return Results.Ok(new
        {
            success = outcome.Success,
            status = outcome.Status,
            error = outcome.Error,
            tenantCount = outcome.TenantCount,
            mailboxCount = outcome.MailboxCount,
            changed = outcome.Changed,
        });
    }

    // ---- /tenants ------------------------------------------------------

    private static async Task<IResult> GetTenants(ISophosStore store, CancellationToken ct)
    {
        var rows = await store.GetTenantsAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(t => new
            {
                tenantId = t.TenantId,
                name = t.Name,
                showAs = t.ShowAs,
                companyCode = t.CompanyCode,
                companyId = t.CompanyId,
                companyName = t.CompanyName,
                status = t.Status,
                m365Connected = t.M365Connected,
                mailboxCount = t.MailboxCount,
                lastSyncedUtc = t.LastSyncedUtc,
            }),
        });
    }

    // ---- /audit --------------------------------------------------------

    private static async Task<IResult> GetAuditLog(
        IIntegrationAuditQuery audit,
        long? cursor,
        int? limit,
        CancellationToken ct)
    {
        var page = await audit.ListAsync(SophosEventTypes.Integration, cursor, limit ?? 50, ct);
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
