using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
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

        // v0.0.28 — manual "Resync contacts" admin escape valve. Triggered
        // from the company-detail page when an admin doesn't want to wait
        // for the slow reconcile loop (default 24h). Respects the contacts
        // pull-toggles — flipping them off then clicking resync is a no-op
        // by design.
        admin.MapPost("/companies/{companyId:guid}/resync-contacts", ResyncCompanyContacts)
            .WithName("ResyncAdsolutCompanyContacts")
            .WithOpenApi();

        // v0.0.30 — Sync coverage. The tile + overview page surface the
        // gaps between the SD-side universe and Adsolut. Admin-only;
        // mutates only via the link-by-uuid + force-push flows below.
        admin.MapGet("/coverage", GetCoverageCounts).WithName("GetAdsolutCoverageCounts").WithOpenApi();
        admin.MapGet("/coverage/companies", GetCoverageCompanies).WithName("GetAdsolutCoverageCompanies").WithOpenApi();
        admin.MapGet("/coverage/contacts", GetCoverageContacts).WithName("GetAdsolutCoverageContacts").WithOpenApi();
        admin.MapPost("/coverage/link/company/{companyId:guid}", LinkCoverageCompany)
            .WithName("LinkAdsolutCoverageCompany").WithOpenApi();
        admin.MapPost("/coverage/link/contact/{linkId:guid}", LinkCoverageContact)
            .WithName("LinkAdsolutCoverageContact").WithOpenApi();
        admin.MapPost("/coverage/force-push/company/{companyId:guid}", ForcePushCoverageCompany)
            .WithName("ForcePushAdsolutCoverageCompany").WithOpenApi();
        admin.MapPost("/coverage/force-push/contact/{linkId:guid}", ForcePushCoverageContact)
            .WithName("ForcePushAdsolutCoverageContact").WithOpenApi();

        admin.MapGet("/debug/lookup", DebugLookup).WithName("AdsolutDebugLookup").WithOpenApi();
        admin.MapGet("/debug/put-preview", DebugPutPreview).WithName("AdsolutDebugPutPreview").WithOpenApi();
        admin.MapPost("/debug/put", DebugPutCustomer).WithName("AdsolutDebugPutCustomer").WithOpenApi();
        admin.MapGet("/debug/access-token", DebugAccessToken).WithName("AdsolutDebugAccessToken").WithOpenApi()
            .RequireRateLimiting("auth");

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
        IAdsolutSyncStateStore syncStateStore,
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

        var state = await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct);

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
        IAdsolutSyncStateStore syncStateStore,
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
                    await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct),
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
        IAdsolutSyncStateStore syncStateStore,
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
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct),
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
        IAdsolutSyncStateStore syncStateStore,
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
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct),
            ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecret(
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        ISettingsService settings,
        IAdsolutConnectionStore connections,
        IAdsolutSyncStateStore syncStateStore,
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
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct),
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
        IAdsolutSyncStateStore syncStateStore,
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
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct),
            ct);
        return Results.Ok(new { administrationId = req.AdministrationId });
    }

    private static async Task<IResult> GetSyncState(
        IAdsolutSyncStateStore syncState,
        ISettingsService settings,
        CancellationToken ct)
    {
        var state = await syncState.GetAsync(ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.SyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 5;
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, intervalMinutes);

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
                nextSyncUtc,
                intervalMinutes,
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
                nextSyncUtc = (DateTime?)nextSyncUtc,
                intervalMinutes,
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

    /// v0.0.28 — manual contacts resync for one company. Looks up the
    /// company's <c>adsolut_id</c>, fetches the full contacts list from
    /// the active dossier, runs the upserter + reconcile pass against it.
    /// Returns the same counters shape the reconcile worker writes to its
    /// audit row, plus the company's adsolut id so the UI can show "synced
    /// against UUID xxxx".
    private static async Task<IResult> ResyncCompanyContacts(
        Guid companyId,
        HttpContext http,
        ISettingsService settings,
        IAdsolutContactsReconciler reconciler,
        IIntegrationAuditLogger integrationAudit,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var pullUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsUpdate, ct);
        var pullCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsCreate, ct);
        var options = new AdsolutContactsSyncOptions(pullUpdate, pullCreate);

        var (actor, role) = ActorContext.Resolve(http);

        // Audit-log the request itself before doing the work, so even a
        // hung/failed run leaves a forensic trail of who clicked.
        await integrationAudit.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ContactsResyncRequested,
            Outcome: IntegrationAuditOutcome.Ok,
            ActorId: actor,
            ActorRole: role,
            Payload: new { companyId, pullUpdate, pullCreate }), ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.contacts.resync_requested",
            Actor: actor,
            ActorRole: role,
            Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);

        var result = await reconciler.ReconcileCompanyAsync(companyId, options, ct);
        if (result is null)
        {
            return Results.NotFound(new
            {
                error = "company_not_linked",
                message = "Company is not linked to Adsolut, the integration is not connected, or no dossier is selected.",
            });
        }

        return Results.Ok(new
        {
            companyId,
            pullUpdate,
            pullCreate,
            result.CompaniesScanned,
            result.ContactsSeen,
            result.ContactsCreated,
            result.ContactsUpdated,
            result.ContactsSkippedNoChange,
            result.ContactsSkippedLocalNewer,
            result.ContactsSkippedToggleOff,
            result.ContactsSkippedNoEmail,
            result.ContactsSkippedLinkConflict,
            result.LinksReconciled,
        });
    }

    public sealed record SelectAdministrationRequest([property: Required] Guid AdministrationId);

    // ---- /debug/lookup --------------------------------------------------
    //
    // Diagnostic-only: lets an admin probe Adsolut's /customers or /suppliers
    // list-endpoint with a Code= filter from the settings UI and inspect the
    // raw JSON response. Useful for confirming what fields WK actually sends
    // for a specific relation before we wire them into the upserter. We do
    // NOT proxy arbitrary paths — kind is a fixed enum and only the Code
    // filter is forwarded — so this cannot become a generic "any-API" hole.

    private static readonly HashSet<string> AllowedDebugKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "customer",
        "supplier",
    };

    private static async Task<IResult> DebugLookup(
        string? kind,
        string? code,
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || !AllowedDebugKinds.Contains(kind))
        {
            return Results.BadRequest(new { error = "invalid_kind", message = "kind must be 'customer' or 'supplier'." });
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.BadRequest(new { error = "missing_code" });
        }
        var trimmed = code.Trim();
        if (trimmed.Length > 32)
        {
            return Results.BadRequest(new { error = "invalid_code", message = "code must be 32 characters or fewer." });
        }
        // Restrict to the alphabet Adsolut uses for relation codes (and a
        // few common separators). Keeps the upstream URL trivially safe and
        // blocks "code=&Limit=999" style escape attempts.
        foreach (var ch in trimmed)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '/'))
            {
                return Results.BadRequest(new { error = "invalid_code", message = "code may contain letters, digits, '-', '_', '.', '/' only." });
            }
        }

        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var resource = string.Equals(kind, "supplier", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/{resource}?Code={Uri.EscapeDataString(trimmed)}&Limit=10";

        try
        {
            var raw = await invoker.SendRawAsync(
                eventType: AdsolutEventTypes.DebugLookup,
                buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
                auditPayload: new { kind = resource, code = trimmed, administrationId },
                ct: ct);
            return Results.Ok(new
            {
                status = raw.Status,
                requestUrl = raw.RequestUrl,
                upstreamErrorCode = raw.UpstreamErrorCode,
                body = raw.Body,
            });
        }
        catch (AdsolutApiException ex)
        {
            // Refresh / transport failure — no upstream body to show. Surface
            // it in the same envelope so the UI does not need a second code
            // path; status 0 signals "never reached Adsolut".
            return Results.Ok(new
            {
                status = ex.HttpStatus ?? 0,
                requestUrl = url,
                upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                body = ex.Message,
            });
        }
    }

    // ---- /debug/put-preview + /debug/put ------------------------------
    //
    // Postman-lite for the customers PUT path. The preview endpoint does
    // a single GET on /customers/{id} and runs the response through
    // AdsolutCustomersWriteClient.BuildUpdateBody(getJson, overlay: null,
    // customerId) so the admin sees the exact body the pusher would
    // produce for a no-edit round-trip. They can then optionally edit
    // that body in-browser and POST it back via /debug/put, which forwards
    // it verbatim as the request body of a PUT to /customers/{id}.
    //
    // Why two endpoints instead of one preview-and-send: the admin must
    // be able to see + (optionally) tweak the body before any mutation
    // hits Adsolut. A single round-trip would either skip the review step
    // or force an "are you sure?" dialog, neither matching the Postman-
    // workflow intent.
    //
    // Scope guard: the URL path id is the only mutable surface; everything
    // else is admin-gated and audited as debug.put_preview / debug.customer_put.
    // The supplied PUT body is sent verbatim — the admin owns the contract
    // they are testing.

    private static async Task<IResult> DebugPutPreview(
        Guid customerId,
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        if (customerId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_customer_id" });
        }

        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}";

        AdsolutRawResponse raw;
        try
        {
            raw = await invoker.SendRawAsync(
                eventType: AdsolutEventTypes.DebugPutPreview,
                buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
                auditPayload: new { customerId, administrationId },
                ct: ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Ok(new
            {
                getStatus = ex.HttpStatus ?? 0,
                getRequestUrl = url,
                upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                getBody = ex.Message,
                putUrl = url,
                putBody = (string?)null,
                putBuildError = (string?)null,
            });
        }

        // Only attempt to derive the PUT body when the GET succeeded with
        // a parseable JSON payload. A 4xx/5xx body from Adsolut is usually
        // an error envelope and feeding it into BuildUpdateBody would just
        // throw; surface the failure cleanly so the UI can show "GET
        // failed, no PUT body to preview".
        string? putBody = null;
        string? putBuildError = null;
        if (raw.Status is >= 200 and < 300 && !string.IsNullOrWhiteSpace(raw.Body))
        {
            try
            {
                putBody = AdsolutCustomersWriteClient.BuildUpdateBody(raw.Body, overlay: null, customerId);
            }
            catch (AdsolutApiException ex)
            {
                putBuildError = ex.Message;
            }
            catch (Exception ex)
            {
                putBuildError = $"build_failed: {ex.Message}";
            }
        }

        return Results.Ok(new
        {
            getStatus = raw.Status,
            getRequestUrl = raw.RequestUrl,
            upstreamErrorCode = raw.UpstreamErrorCode,
            getBody = raw.Body,
            putUrl = url,
            putBody,
            putBuildError,
        });
    }

    public sealed record DebugPutRequest(
        [property: Required] Guid CustomerId,
        [property: Required] string Body);

    private static async Task<IResult> DebugPutCustomer(
        [FromBody] DebugPutRequest request,
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        if (request is null || request.CustomerId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_customer_id" });
        }
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Results.BadRequest(new { error = "missing_body" });
        }

        // Defensive parse so a malformed paste doesn't slip through and
        // hit Adsolut as application/json with garbage; their error
        // surface for that case is hostile to debug.
        try
        {
            using var _ = JsonDocument.Parse(request.Body);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", message = ex.Message });
        }

        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{request.CustomerId}";

        try
        {
            var raw = await invoker.SendRawAsync(
                eventType: AdsolutEventTypes.DebugCustomerPut,
                buildRequest: () => new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new StringContent(request.Body, Encoding.UTF8, "application/json"),
                },
                auditPayload: new { customerId = request.CustomerId, administrationId, bodyBytes = request.Body.Length },
                ct: ct);
            return Results.Ok(new
            {
                status = raw.Status,
                requestUrl = raw.RequestUrl,
                upstreamErrorCode = raw.UpstreamErrorCode,
                body = raw.Body,
            });
        }
        catch (AdsolutApiException ex)
        {
            return Results.Ok(new
            {
                status = ex.HttpStatus ?? 0,
                requestUrl = url,
                upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                body = ex.Message,
            });
        }
    }

    // ---- /debug/access-token -------------------------------------------
    //
    // Reveals the current access token so an admin can paste it into a
    // local debug tool (Postman, curl, etc.) for one-off API exploration.
    // Token validity is bounded by the WK access-token lifetime (~1 hour);
    // the admin re-fetches when it expires by clicking the same button.
    //
    // Security:
    //   - Admin-only (group middleware) + rate-limited under the "auth"
    //     policy so brute-force enumeration of the audit_log via repeated
    //     calls is bounded.
    //   - Token value never lands in the audit payload — only the fact of
    //     export plus the access-token expiry. A leaked token can still be
    //     traced back to who exported it via the ClientIp + Actor + the
    //     expiry-window match.
    //   - Token is returned in the JSON body only; not echoed to logs,
    //     headers, or any cache layer. The Servicedesk dev-tools instinct
    //     is to grep network responses for tokens, so the response uses a
    //     boring property name (`accessToken`) and Cache-Control: no-store.

    private static async Task<IResult> DebugAccessToken(
        HttpContext http,
        IAdsolutAccessTokenProvider tokens,
        IAdsolutConnectionStore connections,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }

        string token;
        try
        {
            token = await tokens.GetAccessTokenAsync(ct);
        }
        catch (AdsolutRefreshException ex)
        {
            return Results.BadRequest(new
            {
                error = "refresh_failed",
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            });
        }

        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Adsolut.ApiBaseUrl, ct) ?? string.Empty).TrimEnd('/');

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.AccessTokenExported,
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new
            {
                accessTokenExpiresUtc = connection.AccessTokenExpiresUtc,
                administrationId = connection.AdministrationId,
            }), ct);

        // Cache-Control: no-store so an intermediate proxy cannot cache
        // the token. Should never matter behind our auth-cookie + HTTPS,
        // but defence in depth is cheap.
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        return Results.Ok(new
        {
            accessToken = token,
            accessTokenExpiresUtc = connection.AccessTokenExpiresUtc,
            baseUrl,
            administrationId = connection.AdministrationId,
        });
    }

    // ---- v0.0.30 Sync coverage -----------------------------------------
    //
    // Pure read-side counts + paged list helpers powering the "Sync coverage"
    // tile and the /settings/integrations/adsolut/coverage page. Mutating
    // endpoints (link, force-push) are below the read-side. All admin-gated
    // via the group middleware.

    private static async Task<IResult> GetCoverageCounts(
        IAdsolutCoverageQuery coverage,
        CancellationToken ct)
    {
        var counts = await coverage.GetCountsAsync(ct);
        return Results.Ok(new
        {
            companiesSdOnly = counts.CompaniesSdOnly,
            companiesDrift = counts.CompaniesDrift,
            contactLinksUnsynced = counts.ContactLinksUnsynced,
            contactLinksDrift = counts.ContactLinksDrift,
            contactsPureSd = counts.ContactsPureSd,
        });
    }

    private static async Task<IResult> GetCoverageCompanies(
        string? bucket,
        string? search,
        int? page,
        int? pageSize,
        IAdsolutCoverageQuery coverage,
        CancellationToken ct)
    {
        if (!TryParseCompaniesBucket(bucket, out var parsed))
        {
            return Results.BadRequest(new { error = "invalid_bucket", message = "bucket must be 'sd-only' or 'drift'." });
        }
        var result = await coverage.ListCompaniesAsync(
            parsed, search, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                code = r.Code,
                email = r.Email,
                adsolutId = r.AdsolutId,
                adsolutNumber = r.AdsolutNumber,
                adsolutLastModified = r.AdsolutLastModified,
                updatedUtc = r.UpdatedUtc,
            }),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    private static async Task<IResult> GetCoverageContacts(
        string? bucket,
        string? search,
        int? page,
        int? pageSize,
        IAdsolutCoverageQuery coverage,
        CancellationToken ct)
    {
        if (!TryParseContactsBucket(bucket, out var parsed))
        {
            return Results.BadRequest(new
            {
                error = "invalid_bucket",
                message = "bucket must be 'links-unsynced', 'links-drift' or 'pure-sd'.",
            });
        }
        var result = await coverage.ListContactsAsync(
            parsed, search, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(r => new
            {
                linkId = r.LinkId,
                contactId = r.ContactId,
                email = r.Email,
                firstName = r.FirstName,
                lastName = r.LastName,
                companyId = r.CompanyId,
                companyName = r.CompanyName,
                companyAdsolutId = r.CompanyAdsolutId,
                adsolutContactId = r.AdsolutContactId,
                adsolutLastModified = r.AdsolutLastModified,
                contactUpdatedUtc = r.ContactUpdatedUtc,
            }),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    public sealed record LinkCompanyRequest(
        [property: Required] Guid AdsolutId,
        DateTime? LastModified);

    private static async Task<IResult> LinkCoverageCompany(
        Guid companyId,
        [FromBody] LinkCompanyRequest body,
        HttpContext http,
        [FromServices] Npgsql.NpgsqlDataSource dataSource,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (body is null || body.AdsolutId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_adsolut_id" });
        }

        // Persist adsolut_id + adsolut_last_modified only. We deliberately
        // do not stamp adsolut_synced_hash here: the next push tick will
        // either fire a single PUT (drift on the SD side) or a single pull
        // (drift on the Adsolut side); either way the loop closes within
        // one tick, no "hash-storm". adsolut_number / adsolut_alpha_code
        // arrive on the next pull tick that touches this row.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE companies
               SET adsolut_id            = @AdsolutId,
                   adsolut_last_modified = @LastModified,
                   updated_utc           = COALESCE(@LastModified, now())
             WHERE id = @CompanyId
               AND is_active = TRUE
            """,
            new
            {
                CompanyId = companyId,
                AdsolutId = body.AdsolutId,
                LastModified = body.LastModified,
            },
            cancellationToken: ct));
        if (rows == 0)
        {
            return Results.NotFound(new { error = "company_not_found_or_inactive" });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageLinkCompany,
            Actor: actor,
            ActorRole: role,
            Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { companyId, adsolutId = body.AdsolutId, body.LastModified }), ct);
        return Results.Ok(new { ok = true });
    }

    public sealed record LinkContactRequest(
        [property: Required] Guid AdsolutContactId,
        DateTime? LastModified);

    private static async Task<IResult> LinkCoverageContact(
        Guid linkId,
        [FromBody] LinkContactRequest body,
        HttpContext http,
        [FromServices] Npgsql.NpgsqlDataSource dataSource,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (body is null || body.AdsolutContactId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_adsolut_contact_id" });
        }
        // Stamp adsolut_active=TRUE so the v0.0.28-derived contacts.is_active
        // recompute treats this link as Adsolut-aware for "ever was X" — see
        // Adsolut.md → Lessons learned #5.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE contact_companies cc
               SET adsolut_contact_id    = @AdsolutContactId,
                   adsolut_last_modified = @LastModified,
                   adsolut_active        = TRUE,
                   updated_utc           = COALESCE(@LastModified, now())
              FROM contacts c, companies co
             WHERE cc.id = @LinkId
               AND c.id = cc.contact_id
               AND co.id = cc.company_id
               AND c.is_active = TRUE
               AND co.is_active = TRUE
               AND co.adsolut_id IS NOT NULL
            """,
            new
            {
                LinkId = linkId,
                AdsolutContactId = body.AdsolutContactId,
                LastModified = body.LastModified,
            },
            cancellationToken: ct));
        if (rows == 0)
        {
            return Results.NotFound(new
            {
                error = "link_not_found_or_company_not_linked",
                message = "Link not found, soft-deleted, or its company is not yet linked to Adsolut.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageLinkContact,
            Actor: actor,
            ActorRole: role,
            Target: linkId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { linkId, adsolutContactId = body.AdsolutContactId, body.LastModified }), ct);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> ForcePushCoverageCompany(
        Guid companyId,
        HttpContext http,
        IAdsolutConnectionStore connections,
        IAdsolutCompanyPusher pusher,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }
        var candidate = await pusher.LoadCandidateByIdAsync(companyId, ct);
        if (candidate is null)
        {
            return Results.NotFound(new { error = "company_not_found_or_inactive" });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutPushOutcome outcome;
        try
        {
            // Force both toggles ON so the toggle-gates short-circuit; the
            // hash-no-op + no-drift gates still apply (this is a force on
            // the toggle, not on the upstream call).
            outcome = await pusher.PushOneAsync(
                administrationId,
                candidate,
                new AdsolutPushOptions(true, true),
                ct);
        }
        catch (AdsolutApiException ex)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.CoverageForcePushCompany,
                Actor: actor,
                ActorRole: role,
                Target: companyId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    companyId,
                    outcome = "ApiException",
                    httpStatus = ex.HttpStatus,
                    upstreamErrorCode = ex.UpstreamErrorCode,
                }), ct);
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageForcePushCompany,
            Actor: actor,
            ActorRole: role,
            Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { companyId, outcome = outcome.ToString() }), ct);

        return Results.Ok(new { outcome = outcome.ToString() });
    }

    private static async Task<IResult> ForcePushCoverageContact(
        Guid linkId,
        HttpContext http,
        IAdsolutConnectionStore connections,
        IAdsolutContactPusher pusher,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }
        var candidate = await pusher.LoadCandidateByLinkIdAsync(linkId, ct);
        if (candidate is null)
        {
            return Results.NotFound(new
            {
                error = "link_not_found",
                message = "Link not found, soft-deleted, or its company is not Adsolut-linked.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutContactPushOutcome outcome;
        try
        {
            outcome = await pusher.PushOneAsync(
                administrationId,
                candidate,
                new AdsolutContactsPushOptions(true, true),
                ct);
        }
        catch (AdsolutApiException ex)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.CoverageForcePushContact,
                Actor: actor,
                ActorRole: role,
                Target: linkId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    linkId,
                    outcome = "ApiException",
                    httpStatus = ex.HttpStatus,
                    upstreamErrorCode = ex.UpstreamErrorCode,
                }), ct);
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageForcePushContact,
            Actor: actor,
            ActorRole: role,
            Target: linkId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { linkId, outcome = outcome.ToString() }), ct);

        return Results.Ok(new { outcome = outcome.ToString() });
    }

    private static bool TryParseCompaniesBucket(string? raw, out AdsolutCoverageCompaniesBucket bucket)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "sd-only":
                bucket = AdsolutCoverageCompaniesBucket.SdOnly;
                return true;
            case "drift":
                bucket = AdsolutCoverageCompaniesBucket.Drift;
                return true;
            default:
                bucket = default;
                return false;
        }
    }

    private static bool TryParseContactsBucket(string? raw, out AdsolutCoverageContactsBucket bucket)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "links-unsynced":
                bucket = AdsolutCoverageContactsBucket.LinksUnsynced;
                return true;
            case "links-drift":
                bucket = AdsolutCoverageContactsBucket.LinksDrift;
                return true;
            case "pure-sd":
                bucket = AdsolutCoverageContactsBucket.PureSd;
                return true;
            default:
                bucket = default;
                return false;
        }
    }

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
