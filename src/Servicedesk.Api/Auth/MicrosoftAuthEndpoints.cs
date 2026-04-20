using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Extensions;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Auth;

public static class MicrosoftAuthEndpoints
{
    // HTTP-only, Secure, SameSite=Lax so the browser replays it on the
    // top-level redirect back from login.microsoftonline.com. Strict
    // would drop the cookie on the cross-site redirect and the callback
    // would see no state.
    private const string ChallengeCookieName = "sd_ms_challenge";
    private const int ChallengeCookieLifetimeMinutes = 10;

    // DataProtection purpose string — versioned so rotating the format
    // (e.g. moving to a signed JWT) is a trivial data migration.
    private const string DataProtectionPurpose = "Servicedesk.Auth.Microsoft.Challenge.v1";

    // Amr claim value. "ext" = external-identity-provider (no MFA step
    // at our side — Azure AD handled that end). Agents who need MFA
    // enforce it on the Azure AD side via Conditional Access.
    private const string AmrExternal = "ext";

    public static IEndpointRouteBuilder MapMicrosoftAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/microsoft").WithTags("Auth");

        group.MapGet("/challenge", StartChallenge)
            .WithName("AuthMicrosoftChallenge")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapGet("/callback", HandleCallback)
            .WithName("AuthMicrosoftCallback")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        return app;
    }

    // ---- /challenge --------------------------------------------------------

    private static async Task<IResult> StartChallenge(
        HttpContext httpContext,
        string? returnUrl,
        IMicrosoftAuthService auth,
        ISettingsService settings,
        IDataProtectionProvider dataProtection,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Auth.MicrosoftEnabled, ct);
        if (!enabled)
        {
            // Intentionally 404 — we do not want to leak that the feature
            // exists but is off; makes port-scanning useless.
            return Results.NotFound();
        }

        var publicBase = await ResolvePublicBaseAsync(httpContext, settings, ct);
        var redirectUri = $"{publicBase}/api/auth/microsoft/callback";
        var intent = await auth.CreateChallengeAsync(redirectUri, SafeReturnUrl(returnUrl), ct);

        WriteChallengeCookie(httpContext, dataProtection, intent);

        return Results.Redirect(intent.AuthorizeUrl);
    }

    // ---- /callback ---------------------------------------------------------

    private static async Task<IResult> HandleCallback(
        HttpContext httpContext,
        string? code,
        string? state,
        string? error,
        string? error_description,
        IMicrosoftAuthService auth,
        IUserService users,
        ISessionService sessions,
        ISettingsService settings,
        IAuditLogger audit,
        IDataProtectionProvider dataProtection,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Auth.MicrosoftEnabled, ct);
        if (!enabled)
        {
            return Results.NotFound();
        }

        var publicBase = await ResolvePublicBaseAsync(httpContext, settings, ct);

        // Read + clear the intent cookie up front — regardless of
        // outcome, the cookie is single-use so a stolen value can't be
        // replayed.
        var intent = ReadChallengeCookie(httpContext, dataProtection);
        ClearChallengeCookie(httpContext);

        if (intent is null)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.MicrosoftLoginFailedCallback,
                Actor: "anon",
                ActorRole: "anon",
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = "missing_intent_cookie" }), ct);
            return RedirectToLogin(publicBase, "missing_intent");
        }

        // Azure AD may redirect back with ?error=access_denied when the
        // user cancels on the consent screen. Pass that through as a
        // clean login-page message instead of a 500.
        if (!string.IsNullOrEmpty(error))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.MicrosoftLoginFailedCallback,
                Actor: "anon",
                ActorRole: "anon",
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { error, error_description }), ct);
            return RedirectToLogin(publicBase, error);
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.MicrosoftLoginFailedCallback,
                Actor: "anon",
                ActorRole: "anon",
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = "missing_code_or_state" }), ct);
            return RedirectToLogin(publicBase, "invalid_callback");
        }

        if (!string.Equals(state, intent.State, StringComparison.Ordinal))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.MicrosoftLoginFailedCallback,
                Actor: "anon",
                ActorRole: "anon",
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = "state_mismatch" }), ct);
            return RedirectToLogin(publicBase, "state_mismatch");
        }

        var redirectUri = $"{publicBase}/api/auth/microsoft/callback";
        var result = await auth.ValidateCallbackAsync(code, redirectUri, intent, ct);

        switch (result)
        {
            case CallbackResult.Success success:
                await users.RecordSuccessfulLoginAsync(success.User.Id, ct);
                await EstablishSessionAsync(httpContext, success.User, sessions, settings, ct);
                await audit.LogAsync(new AuditEvent(
                    EventType: AuthEventTypes.MicrosoftLoginSuccess,
                    Actor: success.User.Email,
                    ActorRole: success.User.RoleName,
                    Target: success.User.Id.ToString(),
                    ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                    Payload: new { oid = success.Oid }), ct);
                return Results.Redirect(BuildFrontendUrl(publicBase, intent.ReturnUrl ?? "/"));

            case CallbackResult.Rejected rejected:
                var (eventType, loginErrorCode) = MapRejection(rejected.Reason);
                await audit.LogAsync(new AuditEvent(
                    EventType: eventType,
                    Actor: rejected.PreferredUsername ?? "anon",
                    ActorRole: "anon",
                    Target: rejected.Oid,
                    ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                    Payload: new { reason = rejected.Reason.ToString(), detail = rejected.Detail }), ct);
                return RedirectToLogin(publicBase, loginErrorCode);

            default:
                // Should never happen — the sealed hierarchy above covers
                // every case. Kept for exhaustiveness-in-case-of-refactor.
                return RedirectToLogin(publicBase, "unknown");
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static (string EventType, string LoginErrorCode) MapRejection(CallbackRejectReason reason) => reason switch
    {
        CallbackRejectReason.InvalidToken => (AuthEventTypes.MicrosoftLoginFailedCallback, "invalid_token"),
        CallbackRejectReason.CodeExchangeFailed => (AuthEventTypes.MicrosoftLoginFailedCallback, "code_exchange_failed"),
        CallbackRejectReason.UnknownOid => (AuthEventTypes.MicrosoftLoginRejectedUnknown, "not_authorized"),
        CallbackRejectReason.CustomerRole => (AuthEventTypes.MicrosoftLoginRejectedCustomer, "not_authorized"),
        CallbackRejectReason.Inactive => (AuthEventTypes.MicrosoftLoginRejectedInactive, "inactive"),
        CallbackRejectReason.AccountDisabled => (AuthEventTypes.MicrosoftLoginRejectedDisabled, "disabled"),
        _ => (AuthEventTypes.MicrosoftLoginFailedCallback, "unknown"),
    };

    /// Returns the origin the frontend is served from. Prefers the
    /// <c>App.PublicBaseUrl</c> setting (e.g. <c>https://desk.company.com</c>
    /// in production, <c>http://localhost:5173</c> in dev where Vite runs
    /// on a separate port from Kestrel). Falls back to the current
    /// request's scheme + host when the setting is empty — which is the
    /// correct answer on a single-origin install behind nginx. Trailing
    /// slash is stripped so callers can safely concatenate paths.
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

    private static string BuildFrontendUrl(string publicBase, string path)
    {
        var normalisedPath = path.StartsWith('/') ? path : "/" + path;
        return publicBase + normalisedPath;
    }

    /// Only accept relative, path-shaped return URLs. Absolute URLs or
    /// protocol-relative values are rejected to stop open-redirect abuse.
    private static string? SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)) return null;
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }
        return returnUrl;
    }

    private static IResult RedirectToLogin(string publicBase, string errorCode)
    {
        // Land on the SPA's login page with an ?error=… so the UI can
        // render a contextual message instead of a generic "failed".
        // Absolute URL rooted at publicBase so this works when Kestrel
        // and the SPA live on different origins in dev.
        return Results.Redirect($"{publicBase}/login?error={Uri.EscapeDataString(errorCode)}");
    }

    // ---- intent-cookie plumbing --------------------------------------------

    private static void WriteChallengeCookie(HttpContext httpContext, IDataProtectionProvider dp, ChallengeIntent intent)
    {
        var protector = dp.CreateProtector(DataProtectionPurpose);
        var payload = JsonSerializer.Serialize(new CookiePayload(
            intent.State, intent.Nonce, intent.CodeVerifier, intent.ReturnUrl));
        var protectedValue = protector.Protect(payload);

        httpContext.Response.Cookies.Append(ChallengeCookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth/microsoft",
            Expires = DateTimeOffset.UtcNow.AddMinutes(ChallengeCookieLifetimeMinutes),
        });
    }

    private static ChallengeIntent? ReadChallengeCookie(HttpContext httpContext, IDataProtectionProvider dp)
    {
        var raw = httpContext.Request.Cookies[ChallengeCookieName];
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            var protector = dp.CreateProtector(DataProtectionPurpose);
            var json = protector.Unprotect(raw);
            var payload = JsonSerializer.Deserialize<CookiePayload>(json);
            if (payload is null) return null;
            return new ChallengeIntent(
                AuthorizeUrl: string.Empty, // unused on callback
                State: payload.State,
                Nonce: payload.Nonce,
                CodeVerifier: payload.CodeVerifier,
                ReturnUrl: payload.ReturnUrl);
        }
        catch (Exception)
        {
            // Tampered, expired, or key rotated — treat as missing.
            return null;
        }
    }

    private static void ClearChallengeCookie(HttpContext httpContext)
    {
        httpContext.Response.Cookies.Delete(ChallengeCookieName, new CookieOptions
        {
            Path = "/api/auth/microsoft",
        });
    }

    private sealed record CookiePayload(string State, string Nonce, string CodeVerifier, string? ReturnUrl);

    // ---- session-mint ------------------------------------------------------

    private static async Task EstablishSessionAsync(
        HttpContext httpContext,
        ApplicationUser user,
        ISessionService sessions,
        ISettingsService settings,
        CancellationToken ct)
    {
        var lifetimeHours = await settings.GetAsync<int>(SettingKeys.Security.SessionLifetimeHours, ct);
        var cookieName = await settings.GetAsync<string>(SettingKeys.Security.SessionCookieName, ct);
        var sessionId = await sessions.CreateAsync(
            user.Id,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            TimeSpan.FromHours(lifetimeHours),
            AmrExternal,
            ct);

        var secure = httpContext.Request.IsHttps;
        var expires = DateTimeOffset.UtcNow.AddHours(lifetimeHours);

        httpContext.Response.Cookies.Append(cookieName, sessionId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
        });

        var csrfToken = DoubleSubmitCsrfMiddleware.GenerateToken();
        httpContext.Response.Cookies.Append(DoubleSubmitCsrfMiddleware.CookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
        });
    }
}
