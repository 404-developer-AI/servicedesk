using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
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
        var hasRefreshToken = await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        var connection = await connections.GetAsync(ct);

        var publicBase = await ResolvePublicBaseAsync(http, settings, ct);
        var redirectUri = publicBase + CallbackPath;

        string state;
        if (string.IsNullOrEmpty(clientId) || !hasSecret)
        {
            state = "not_configured";
        }
        else if (!hasRefreshToken)
        {
            state = "not_connected";
        }
        else if (connection?.LastRefreshError is not null)
        {
            state = "refresh_failed";
        }
        else
        {
            state = "connected";
        }

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
        });
    }

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
        IAuditLogger audit,
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

        var result = await auth.CompleteCallbackAsync(code, redirectUri, ct);
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
        IAuditLogger audit,
        CancellationToken ct)
    {
        await auth.DisconnectAsync(ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.Disconnected,
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
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
            var refreshed = await auth.RefreshAccessTokenAsync(ct);
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
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecret(
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
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
        return Results.NoContent();
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
