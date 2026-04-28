using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Adsolut OAuth integration. The /authorize and
/// management endpoints are admin-gated; /callback is intentionally
/// anonymous because the cross-site redirect from Wolters Kluwer drops
/// the SameSite=Strict session cookie, so trust on /callback is carried
/// by the encrypted intent cookie instead.
public static class AdsolutEndpoints
{
    private const string IntentCookieName = "sd_adsolut_intent";
    private const int IntentCookieLifetimeMinutes = 10;
    private const string DataProtectionPurpose = "Servicedesk.Integrations.Adsolut.Intent.v1";
    private const string CallbackPath = "/api/admin/integrations/adsolut/callback";
    private const string SpaReturnPath = "/settings/integrations/adsolut";

    public static IEndpointRouteBuilder MapAdsolutEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/adsolut")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetAdsolutStatus").WithOpenApi();
        admin.MapPost("/authorize", StartAuthorize).WithName("StartAdsolutAuthorize").WithOpenApi();
        admin.MapPost("/disconnect", Disconnect).WithName("DisconnectAdsolut").WithOpenApi();
        admin.MapPost("/refresh", TestRefresh).WithName("RefreshAdsolutToken").WithOpenApi();

        admin.MapGet("/secret", GetSecretStatus).WithName("GetAdsolutSecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetAdsolutSecret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteAdsolutSecret").WithOpenApi();

        admin.MapGet("/audit", GetAuditLog).WithName("GetAdsolutAuditLog").WithOpenApi();

        // v0.0.26 — Companies pull. The dossier picker (admin chooses
        // which Adsolut administration to sync) and the on-demand
        // "Sync now" + sync-state surface for the UI.
        admin.MapGet("/administrations", ListAdministrations).WithName("ListAdsolutAdministrations").WithOpenApi();
        admin.MapPost("/administration", SelectAdministration).WithName("SelectAdsolutAdministration").WithOpenApi();
        admin.MapGet("/sync", GetSyncState).WithName("GetAdsolutSyncState").WithOpenApi();
        admin.MapPost("/sync", TriggerSyncNow).WithName("TriggerAdsolutSyncNow").WithOpenApi();

        // Anonymous — see header comment. Validation happens via the
        // tamper-evident intent cookie + the upstream code exchange.
        app.MapGet(CallbackPath, HandleCallback)
            .WithName("AdsolutCallback")
            .WithTags("Integrations")
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            .WithOpenApi();

        return app;
    }

    // ---- /status --------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        HttpContext http,
        CancellationToken ct)
    {
        var environment = await settings.GetAsync<string>(SettingKeys.Adsolut.Environment, ct);
        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var scopes = await settings.GetAsync<string>(SettingKeys.Adsolut.Scopes, ct);
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var connection = await connections.GetAsync(ct);

        var publicBase = await ResolvePublicBaseAsync(http, settings, ct);
        var redirectUri = publicBase + CallbackPath;

        var state = await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct);

        // v0.0.26 (post-feature) — surface whether the saved Settings.Adsolut.Scopes
        // string differs from the scope-list bound to the current refresh
        // token. True = admin saved new scopes via the picker but did not
        // reconnect yet; the next API call will fail with insufficient_scope.
        // Set-equality (order-insensitive, whitespace-collapsed) so a cosmetic
        // re-arrangement does not flag a healthy connection.
        var scopesNeedReconnect = ScopesDiffer(scopes, connection?.ScopesAtAuthorize);

        return Results.Ok(new
        {
            state,
            environment,
            clientIdConfigured = !string.IsNullOrEmpty(clientId),
            clientSecretConfigured = hasSecret,
            scopes,
            redirectUri,
            authorizedSubject = connection?.AuthorizedSubject,
            authorizedEmail = connection?.AuthorizedEmail,
            authorizedUtc = connection?.AuthorizedUtc,
            lastRefreshedUtc = connection?.LastRefreshedUtc,
            accessTokenExpiresUtc = connection?.AccessTokenExpiresUtc,
            lastRefreshError = connection?.LastRefreshError,
            lastRefreshErrorUtc = connection?.LastRefreshErrorUtc,
            administrationId = connection?.AdministrationId,
            scopesAtAuthorize = connection?.ScopesAtAuthorize,
            scopesNeedReconnect,
        });
    }

    /// True when the saved Settings.Adsolut.Scopes string does not match the
    /// scope set bound to the current refresh token. Returns false when there
    /// is no connection yet (<paramref name="atAuthorize"/> is null) — the
    /// regular not_connected pill covers that case. Comparison is by token
    /// set, not raw string, so an admin re-saving the same scopes in a new
    /// order does not flag a healthy connection.
    private static bool ScopesDiffer(string? saved, string? atAuthorize)
    {
        if (string.IsNullOrWhiteSpace(atAuthorize)) return false;
        var savedSet = TokenizeScopes(saved);
        var atAuthSet = TokenizeScopes(atAuthorize);
        return !savedSet.SetEquals(atAuthSet);
    }

    private static HashSet<string> TokenizeScopes(string? raw) =>
        new((raw ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

    // ---- /authorize -----------------------------------------------------

    private static async Task<IResult> StartAuthorize(
        HttpContext http,
        IAdsolutAuthService auth,
        ISettingsService settings,
        IDataProtectionProvider dataProtection,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var publicBase = await ResolvePublicBaseAsync(http, settings, ct);
        var redirectUri = publicBase + CallbackPath;

        AdsolutChallengeIntent intent;
        try
        {
            intent = await auth.CreateChallengeAsync(redirectUri, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = "not_configured", message = ex.Message });
        }

        var (actor, role) = ActorContext.Resolve(http);
        WriteIntentCookie(http, dataProtection, new IntentPayload(intent.State, actor, role));

        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.ConnectStarted,
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { redirectUri }), ct);

        return Results.Ok(new { authorizeUrl = intent.AuthorizeUrl });
    }

    // ---- /callback ------------------------------------------------------

    private static async Task<IResult> HandleCallback(
        HttpContext http,
        string? code,
        string? state,
        string? error,
        string? error_description,
        IAdsolutAuthService auth,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        IAuditLogger audit,
        IIntegrationStatusNotifier statusNotifier,
        IDataProtectionProvider dataProtection,
        CancellationToken ct)
    {
        var publicBase = await ResolvePublicBaseAsync(http, settings, ct);
        var redirectUri = publicBase + CallbackPath;

        var intent = ReadIntentCookie(http, dataProtection);
        ClearIntentCookie(http);

        if (intent is null)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.ConnectFailed,
                Actor: "anon",
                ActorRole: "anon",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = "missing_intent_cookie" }), ct);
            return RedirectToSpa(publicBase, "missing_intent");
        }

        if (!string.IsNullOrEmpty(error))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.ConnectFailed,
                Actor: intent.Actor,
                ActorRole: intent.ActorRole,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { error, error_description }), ct);
            return RedirectToSpa(publicBase, error);
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.ConnectFailed,
                Actor: intent.Actor,
                ActorRole: intent.ActorRole,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = "missing_code_or_state" }), ct);
            return RedirectToSpa(publicBase, "invalid_callback");
        }

        if (!string.Equals(state, intent.State, StringComparison.Ordinal))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.ConnectFailed,
                Actor: intent.Actor,
                ActorRole: intent.ActorRole,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = "state_mismatch" }), ct);
            return RedirectToSpa(publicBase, "state_mismatch");
        }

        var result = await auth.CompleteCallbackAsync(code, redirectUri, intent.Actor, intent.ActorRole, ct);
        switch (result)
        {
            case AdsolutCallbackResult.Success success:
                await audit.LogAsync(new AuditEvent(
                    EventType: AdsolutEventTypes.ConnectSucceeded,
                    Actor: intent.Actor,
                    ActorRole: intent.ActorRole,
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new
                    {
                        authorizedSubject = success.AuthorizedSubject,
                        authorizedEmail = success.AuthorizedEmail,
                    }), ct);
                await statusNotifier.NotifyStatusChangedAsync(
                    AdsolutEventTypes.Integration,
                    await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct),
                    ct);
                return RedirectToSpa(publicBase, "connected");

            case AdsolutCallbackResult.Rejected rejected:
                await audit.LogAsync(new AuditEvent(
                    EventType: AdsolutEventTypes.ConnectFailed,
                    Actor: intent.Actor,
                    ActorRole: intent.ActorRole,
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { reason = rejected.Reason.ToString(), detail = rejected.Detail }), ct);
                return RedirectToSpa(publicBase, MapRejectCode(rejected.Reason));

            default:
                return RedirectToSpa(publicBase, "unknown");
        }
    }

    // ---- /disconnect ----------------------------------------------------

    private static async Task<IResult> Disconnect(
        HttpContext http,
        IAdsolutAuthService auth,
        IAdsolutAdministrationsClient adminClient,
        IAdsolutAccessTokenProvider tokens,
        IAuditLogger audit,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        IIntegrationStatusNotifier statusNotifier,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // v0.0.26 — deactivate the integration on the chosen Adsolut
        // administration BEFORE wiping the tokens. WK docs warn that
        // leaving an integration active has financial impact for the
        // customer; doing this after Disconnect would have no token to
        // call with. Best-effort: a transport failure here must not
        // block the local disconnect, otherwise a stuck WK endpoint
        // would orphan a refresh token forever.
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is Guid adminId)
        {
            try
            {
                await adminClient.DeactivateAsync(adminId, ct);
            }
            catch (AdsolutApiException ex)
            {
                loggerFactory.CreateLogger("AdsolutEndpoints").LogWarning(ex,
                    "Adsolut deactivate-on-disconnect failed for administration {AdministrationId}; continuing with local disconnect.",
                    adminId);
            }
        }
        tokens.Invalidate();

        await auth.DisconnectAsync(ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.Disconnected,
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        await statusNotifier.NotifyStatusChangedAsync(
            AdsolutEventTypes.Integration,
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct),
            ct);
        return Results.NoContent();
    }

    // ---- /refresh (smoke test) ------------------------------------------

    private static async Task<IResult> TestRefresh(
        HttpContext http,
        IAdsolutAuthService auth,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        try
        {
            var refreshed = await auth.RefreshAccessTokenAsync("admin_test", actor, role, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.TokenRefreshed,
                Actor: actor,
                ActorRole: role,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { expiresUtc = refreshed.ExpiresUtc }), ct);
            return Results.Ok(new { ok = true, expiresUtc = refreshed.ExpiresUtc });
        }
        catch (AdsolutRefreshException ex)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.TokenRefreshFailed,
                Actor: actor,
                ActorRole: role,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    upstreamErrorCode = ex.UpstreamErrorCode,
                    requiresReconnect = ex.RequiresReconnect,
                }), ct);
            return Results.Ok(new
            {
                ok = false,
                upstreamErrorCode = ex.UpstreamErrorCode,
                requiresReconnect = ex.RequiresReconnect,
                message = ex.Message,
            });
        }
    }

    // ---- /secret CRUD ---------------------------------------------------

    private static async Task<IResult> GetSecretStatus(IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct) });

    private static async Task<IResult> SetSecret(
        [FromBody] SetSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        ISettingsService settings,
        IAdsolutConnectionStore connections,
        IIntegrationStatusNotifier statusNotifier,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "Client secret is required." });
        await secrets.SetAsync(ProtectedSecretKeys.AdsolutClientSecret, req.Value.Trim(), ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.ClientSecretUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.AdsolutClientSecret,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = true }), ct);
        await statusNotifier.NotifyStatusChangedAsync(
            AdsolutEventTypes.Integration,
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct),
            ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecret(
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        ISettingsService settings,
        IAdsolutConnectionStore connections,
        IIntegrationStatusNotifier statusNotifier,
        CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.ClientSecretDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.AdsolutClientSecret,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        await statusNotifier.NotifyStatusChangedAsync(
            AdsolutEventTypes.Integration,
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct),
            ct);
        return Results.NoContent();
    }

    // ---- /audit ---------------------------------------------------------

    private static async Task<IResult> GetAuditLog(
        IIntegrationAuditQuery audit,
        long? cursor,
        int? limit,
        CancellationToken ct)
    {
        // 50 default keeps the table on the detail page legible without
        // scrolling; the cursor lets the UI walk older rows on demand.
        var page = await audit.ListAsync(AdsolutEventTypes.Integration, cursor, limit ?? 50, ct);
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

    // ---- v0.0.26 administrations + sync --------------------------------

    private static async Task<IResult> ListAdministrations(
        IAdsolutAdministrationsClient adminClient,
        IAdsolutConnectionStore connections,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        try
        {
            var items = await adminClient.ListAsync(ct);
            return Results.Ok(new
            {
                items = items.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    code = a.Code,
                }),
                selectedId = connection.AdministrationId,
            });
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }
    }

    private static async Task<IResult> SelectAdministration(
        [FromBody] SelectAdministrationRequest req,
        HttpContext http,
        IAdsolutAdministrationsClient adminClient,
        IAdsolutConnectionStore connections,
        IAuditLogger audit,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IIntegrationStatusNotifier statusNotifier,
        CancellationToken ct)
    {
        if (req.AdministrationId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_administration_id" });
        }
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }

        try
        {
            await adminClient.ActivateAsync(req.AdministrationId, ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "activate_failed",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await connections.SaveAsync(connection with { AdministrationId = req.AdministrationId }, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.administration.selected",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { administrationId = req.AdministrationId }), ct);
        await statusNotifier.NotifyStatusChangedAsync(
            AdsolutEventTypes.Integration,
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct),
            ct);
        return Results.Ok(new { administrationId = req.AdministrationId });
    }

    private static async Task<IResult> GetSyncState(
        IAdsolutSyncStateStore syncState,
        CancellationToken ct)
    {
        var state = await syncState.GetAsync(ct);
        return Results.Ok(state is null
            ? new
            {
                lastFullSyncUtc = (DateTime?)null,
                lastDeltaSyncUtc = (DateTime?)null,
                lastError = (string?)null,
                lastErrorUtc = (DateTime?)null,
                companiesSeen = 0,
                companiesUpserted = 0,
                companiesSkippedLoserInConflict = 0,
                updatedUtc = (DateTime?)null,
            }
            : new
            {
                lastFullSyncUtc = state.LastFullSyncUtc,
                lastDeltaSyncUtc = state.LastDeltaSyncUtc,
                lastError = state.LastError,
                lastErrorUtc = state.LastErrorUtc,
                companiesSeen = state.CompaniesSeen,
                companiesUpserted = state.CompaniesUpserted,
                companiesSkippedLoserInConflict = state.CompaniesSkippedLoserInConflict,
                updatedUtc = (DateTime?)state.UpdatedUtc,
            });
    }

    private static async Task<IResult> TriggerSyncNow(
        HttpContext http,
        IAdsolutSyncWorkerSignal signal,
        IAuditLogger audit,
        CancellationToken ct)
    {
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.sync.requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    public sealed record SelectAdministrationRequest([property: Required] Guid AdministrationId);

    // ---- helpers --------------------------------------------------------

    private static string MapRejectCode(AdsolutCallbackRejectReason reason) => reason switch
    {
        AdsolutCallbackRejectReason.NotConfigured => "not_configured",
        AdsolutCallbackRejectReason.CodeExchangeFailed => "code_exchange_failed",
        AdsolutCallbackRejectReason.InvalidTokenResponse => "invalid_token_response",
        AdsolutCallbackRejectReason.InvalidIdToken => "invalid_id_token",
        _ => "unknown",
    };

    private static async Task<string> ResolvePublicBaseAsync(
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

    private static IResult RedirectToSpa(string publicBase, string statusCode) =>
        Results.Redirect($"{publicBase}{SpaReturnPath}?status={Uri.EscapeDataString(statusCode)}");

    // ---- intent-cookie plumbing ----------------------------------------

    private static void WriteIntentCookie(HttpContext http, IDataProtectionProvider dp, IntentPayload payload)
    {
        var protector = dp.CreateProtector(DataProtectionPurpose);
        var json = JsonSerializer.Serialize(payload);
        var protectedValue = protector.Protect(json);

        // SameSite=Lax: the cross-site redirect from login.wolterskluwer.eu
        // back to /callback is a top-level GET navigation, so Lax sends
        // the cookie. Strict would drop it.
        http.Response.Cookies.Append(IntentCookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/api/admin/integrations/adsolut",
            Expires = DateTimeOffset.UtcNow.AddMinutes(IntentCookieLifetimeMinutes),
        });
    }

    private static IntentPayload? ReadIntentCookie(HttpContext http, IDataProtectionProvider dp)
    {
        var raw = http.Request.Cookies[IntentCookieName];
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var protector = dp.CreateProtector(DataProtectionPurpose);
            var json = protector.Unprotect(raw);
            return JsonSerializer.Deserialize<IntentPayload>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ClearIntentCookie(HttpContext http)
    {
        http.Response.Cookies.Delete(IntentCookieName, new CookieOptions
        {
            Path = "/api/admin/integrations/adsolut",
        });
    }

    private sealed record IntentPayload(
        [property: JsonPropertyName("s")] string State,
        [property: JsonPropertyName("a")] string Actor,
        [property: JsonPropertyName("r")] string ActorRole);

    public sealed record SetSecretRequest([property: Required] string Value);
}
