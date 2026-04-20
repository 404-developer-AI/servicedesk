using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Auth;

public static class AuthEndpoints
{
    private const string AmrPassword = "pwd";
    private const string AmrPasswordPlusMfa = "pwd+mfa";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/setup/status", GetSetupStatus).WithName("AuthSetupStatus").WithOpenApi();

        group.MapGet("/config", GetAuthConfig).WithName("AuthConfig").WithOpenApi();

        group.MapPost("/setup/create-admin", CreateFirstAdmin)
            .WithName("AuthSetupCreateAdmin")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapPost("/login", Login)
            .WithName("AuthLogin")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapPost("/2fa/verify", VerifyTwoFactor)
            .WithName("AuthTwoFactorVerify")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapPost("/logout", Logout).WithName("AuthLogout").WithOpenApi();

        group.MapGet("/me", Me).WithName("AuthMe").WithOpenApi();

        group.MapPost("/2fa/enroll/begin", BeginTotpEnroll)
            .WithName("AuthTotpBegin")
            .WithOpenApi()
            .RequireAuthorization(AuthorizationPolicies.RequireCustomer);

        group.MapPost("/2fa/enroll/confirm", ConfirmTotpEnroll)
            .WithName("AuthTotpConfirm")
            .WithOpenApi()
            .RequireAuthorization(AuthorizationPolicies.RequireCustomer);

        group.MapPost("/2fa/disable", DisableTotp)
            .WithName("AuthTotpDisable")
            .WithOpenApi()
            .RequireAuthorization(AuthorizationPolicies.RequireCustomer);

        return app;
    }

    // ---- Setup wizard ------------------------------------------------------

    private static async Task<IResult> GetSetupStatus(IUserService users, CancellationToken ct)
    {
        var count = await users.CountAsync(ct);
        return Results.Ok(new { available = count == 0 });
    }

    /// Anonymous feature-flag snapshot consumed by the login page before
    /// an auth session exists. Intentionally minimal — only signals that
    /// an unauthenticated client legitimately needs: whether the M365
    /// button should render, and whether the first-admin setup wizard is
    /// still open. No tenant-id / client-id / secret exposure.
    private static async Task<IResult> GetAuthConfig(ISettingsService settings, IUserService users, CancellationToken ct)
    {
        var microsoftEnabled = await settings.GetAsync<bool>(SettingKeys.Auth.MicrosoftEnabled, ct);
        var userCount = await users.CountAsync(ct);
        return Results.Ok(new
        {
            microsoftEnabled,
            setupAvailable = userCount == 0,
        });
    }

    public sealed record CreateAdminRequest(
        [property: Required] string Email,
        [property: Required] string Password);

    private static async Task<IResult> CreateFirstAdmin(
        [FromBody] CreateAdminRequest request,
        HttpContext httpContext,
        IUserService users,
        IPasswordHasher hasher,
        ISessionService sessions,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return Results.BadRequest(new { error = "A valid email is required." });
        }

        var minLength = await settings.GetAsync<int>(SettingKeys.Security.PasswordMinimumLength, ct);
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < minLength)
        {
            return Results.BadRequest(new { error = $"Password must be at least {minLength} characters." });
        }

        var hash = hasher.Hash(request.Password);
        var admin = await users.CreateFirstAdminAsync(request.Email.Trim(), hash, ct);
        if (admin is null)
        {
            return Results.NotFound(new { error = "Setup is no longer available." });
        }

        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.SetupWizardUsed,
            Actor: admin.Email,
            ActorRole: admin.RoleName,
            Target: admin.Id.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);

        await EstablishSessionAsync(httpContext, admin, AmrPassword, sessions, settings, ct);

        return Results.Ok(new { email = admin.Email, role = admin.RoleName });
    }

    // ---- Login + 2FA -------------------------------------------------------

    public sealed record LoginRequest(
        [property: Required] string Email,
        [property: Required] string Password);

    public sealed record LoginResponse(string Email, string Role, bool TwoFactorRequired);

    private static async Task<IResult> Login(
        [FromBody] LoginRequest request,
        HttpContext httpContext,
        IUserService users,
        IPasswordHasher hasher,
        ITotpService totp,
        ISessionService sessions,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return Results.BadRequest(new { error = "Email and password are required." });
        }

        var user = await users.FindByEmailAsync(request.Email.Trim(), ct);
        if (user is null)
        {
            // Constant-ish time failure: still run the hasher against a throwaway value.
            _ = hasher.Verify("$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", request.Password, out _);
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.LoginFailed,
                Actor: request.Email,
                ActorRole: "anon",
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);
            return Results.Unauthorized();
        }

        if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc > DateTimeOffset.UtcNow)
        {
            return Results.StatusCode(StatusCodes.Status423Locked);
        }

        // Local-login is only available to Local-mode users. A Microsoft
        // user has no password_hash by construction (chk_users_auth_mode
        // enforces this), so we can't run the hasher against it. Same
        // 401-no-details response as a wrong email so a hostile prober
        // can't enumerate which accounts are on M365.
        if (user.AuthMode != AuthModes.Local || string.IsNullOrEmpty(user.PasswordHash) || !user.IsActive)
        {
            _ = hasher.Verify("$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", request.Password, out _);
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.LoginFailed,
                Actor: user.Email,
                ActorRole: user.RoleName,
                Target: user.Id.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = user.IsActive ? "wrong_channel" : "inactive" }), ct);
            return Results.Unauthorized();
        }

        var verified = hasher.Verify(user.PasswordHash, request.Password, out var rehash);
        if (!verified)
        {
            var maxAttempts = await settings.GetAsync<int>(SettingKeys.Security.LockoutMaxAttempts, ct);
            var windowSeconds = await settings.GetAsync<int>(SettingKeys.Security.LockoutWindowSeconds, ct);
            var durationSeconds = await settings.GetAsync<int>(SettingKeys.Security.LockoutDurationSeconds, ct);
            var nowLocked = await users.RecordFailedLoginAsync(user.Id, maxAttempts, windowSeconds, durationSeconds, ct);

            await audit.LogAsync(new AuditEvent(
                EventType: nowLocked ? AuthEventTypes.LoginLockedOut : AuthEventTypes.LoginFailed,
                Actor: user.Email,
                ActorRole: user.RoleName,
                Target: user.Id.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);

            return nowLocked
                ? Results.StatusCode(StatusCodes.Status423Locked)
                : Results.Unauthorized();
        }

        if (rehash)
        {
            await users.UpdatePasswordHashAsync(user.Id, hasher.Hash(request.Password), ct);
        }

        var twoFactorEnabled = await totp.IsEnabledAsync(user.Id, ct);
        await users.RecordSuccessfulLoginAsync(user.Id, ct);

        var amr = twoFactorEnabled ? AmrPassword : AmrPassword;
        await EstablishSessionAsync(httpContext, user, amr, sessions, settings, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.LoginSuccess,
            Actor: user.Email,
            ActorRole: user.RoleName,
            Target: user.Id.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
            Payload: new { twoFactorChallengeRequired = twoFactorEnabled }), ct);

        return Results.Ok(new LoginResponse(user.Email, user.RoleName, twoFactorEnabled));
    }

    public sealed record VerifyTwoFactorRequest([property: Required] string Code);

    private static async Task<IResult> VerifyTwoFactor(
        [FromBody] VerifyTwoFactorRequest request,
        HttpContext httpContext,
        ITotpService totp,
        ISessionService sessions,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var sidClaim = httpContext.User.FindFirst("sid")?.Value;
        var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sidClaim, out var sessionId) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await totp.VerifyAsync(userId, request.Code ?? string.Empty, ct);
        if (result == TwoFactorResult.Rejected)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.TwoFactorChallengeFailed,
                Actor: httpContext.User.Identity?.Name ?? userId.ToString(),
                ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
                Target: userId.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);
            return Results.Unauthorized();
        }

        await sessions.UpgradeAmrAsync(sessionId, AmrPasswordPlusMfa, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.TwoFactorChallengeSuccess,
            Actor: httpContext.User.Identity?.Name ?? userId.ToString(),
            ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
            Target: userId.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
            Payload: new { method = result.ToString() }), ct);

        return Results.Ok();
    }

    // ---- Logout + me -------------------------------------------------------

    private static async Task<IResult> Logout(
        HttpContext httpContext,
        ISessionService sessions,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var cookieName = await settings.GetAsync<string>(SettingKeys.Security.SessionCookieName, ct);
        var cookieValue = httpContext.Request.Cookies[cookieName];
        if (Guid.TryParse(cookieValue, out var sessionId))
        {
            await sessions.RevokeAsync(sessionId, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.Logout,
                Actor: httpContext.User.Identity?.Name ?? "anon",
                ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
                Target: sessionId.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);
        }
        ClearAuthCookies(httpContext, cookieName);
        return Results.Ok();
    }

    private static async Task<IResult> Me(
        HttpContext httpContext,
        ITotpService totp,
        CancellationToken ct)
    {
        var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Ok(new { user = (object?)null, serverTimeUtc = DateTimeOffset.UtcNow });
        }
        var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var role = httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        var amr = httpContext.User.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value ?? AmrPassword;
        var twoFactorEnabled = await totp.IsEnabledAsync(userId, ct);
        return Results.Ok(new
        {
            user = new { id = userId, email, role, amr, twoFactorEnabled },
            serverTimeUtc = DateTimeOffset.UtcNow,
        });
    }

    // ---- TOTP enrollment ---------------------------------------------------

    private static async Task<IResult> BeginTotpEnroll(
        HttpContext httpContext,
        ITotpService totp,
        CancellationToken ct)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null) return Results.Unauthorized();
        var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value ?? userId.Value.ToString();
        var enrollment = await totp.BeginEnrollAsync(userId.Value, email, ct);
        return Results.Ok(new { secret = enrollment.SecretBase32, otpauthUri = enrollment.OtpAuthUri });
    }

    public sealed record ConfirmTotpRequest([property: Required] string Code);

    private static async Task<IResult> ConfirmTotpEnroll(
        [FromBody] ConfirmTotpRequest request,
        HttpContext httpContext,
        ITotpService totp,
        ISessionService sessions,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null) return Results.Unauthorized();
        var codes = await totp.ConfirmEnrollAsync(userId.Value, request.Code ?? string.Empty, ct);
        if (codes is null)
        {
            return Results.BadRequest(new { error = "Invalid verification code." });
        }

        var sidClaim = httpContext.User.FindFirst("sid")?.Value;
        if (Guid.TryParse(sidClaim, out var sessionId))
        {
            await sessions.UpgradeAmrAsync(sessionId, AmrPasswordPlusMfa, ct);
        }

        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.TwoFactorEnrolled,
            Actor: httpContext.User.Identity?.Name ?? userId.Value.ToString(),
            ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
            Target: userId.Value.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);

        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> DisableTotp(
        HttpContext httpContext,
        ITotpService totp,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null) return Results.Unauthorized();
        await totp.DisableAsync(userId.Value, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.TwoFactorDisabled,
            Actor: httpContext.User.Identity?.Name ?? userId.Value.ToString(),
            ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
            Target: userId.Value.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);
        return Results.Ok();
    }

    // ---- Helpers -----------------------------------------------------------

    private static Guid? RequireUserId(HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task EstablishSessionAsync(
        HttpContext httpContext,
        ApplicationUser user,
        string amr,
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
            amr,
            ct);

        var secure = !httpContext.Request.IsHttps ? false : true;
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

    private static void ClearAuthCookies(HttpContext httpContext, string sessionCookieName)
    {
        httpContext.Response.Cookies.Delete(sessionCookieName, new CookieOptions { Path = "/" });
        httpContext.Response.Cookies.Delete(DoubleSubmitCsrfMiddleware.CookieName, new CookieOptions { Path = "/" });
    }
}
