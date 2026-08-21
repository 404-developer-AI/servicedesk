using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Portal;

/// v0.1.0 — the customer-portal authentication surface. Completely separate
/// from /api/auth (agents): own login, mandatory TOTP (enrollment forced at
/// first sign-in), self-registration with email verification + approval,
/// invitation acceptance, password reset. The M365/OIDC flow never accepts
/// customers and the agent login refuses Customer rows.
///
/// Anonymous POSTs are CSRF-exempt (DoubleSubmitCsrfMiddleware.ExemptPrefixes,
/// prefix /api/portal/auth/public/) and rate-limited per IP. Everything
/// answers 404 while Portal.Enabled is off.
public static class PortalAuthEndpoints
{
    public static IEndpointRouteBuilder MapPortalAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous, CSRF-exempt, rate-limited.
        var pub = app.MapGroup("/api/portal/auth/public").WithTags("PortalAuth");
        pub.MapPost("/register", Register).WithName("PortalRegister").WithOpenApi().RequireRateLimiting("portal-register");
        pub.MapPost("/verify-email", VerifyEmail).WithName("PortalVerifyEmail").WithOpenApi().RequireRateLimiting("portal-auth");
        pub.MapPost("/login", Login).WithName("PortalLogin").WithOpenApi().RequireRateLimiting("portal-auth");
        pub.MapPost("/forgot-password", ForgotPassword).WithName("PortalForgotPassword").WithOpenApi().RequireRateLimiting("portal-register");
        pub.MapPost("/reset-password", ResetPassword).WithName("PortalResetPassword").WithOpenApi().RequireRateLimiting("portal-auth");
        pub.MapGet("/invitation", DescribeInvitation).WithName("PortalDescribeInvitation").WithOpenApi().RequireRateLimiting("portal-auth");
        pub.MapPost("/invitation/accept", AcceptInvitation).WithName("PortalAcceptInvitation").WithOpenApi().RequireRateLimiting("portal-auth");

        // Session-bound but deliberately without the RequireCustomer policy:
        // these must run while the session is still "mfa-pending" (the TOTP
        // challenge / forced enrollment). Each handler checks the principal
        // itself (authenticated + role Customer).
        var session = app.MapGroup("/api/portal/auth").WithTags("PortalAuth");
        session.MapPost("/2fa/verify", VerifyTwoFactor).WithName("PortalVerifyTwoFactor").WithOpenApi().RequireRateLimiting("portal-auth");
        session.MapPost("/2fa/enroll/begin", BeginEnroll).WithName("PortalEnrollBegin").WithOpenApi();
        session.MapPost("/2fa/enroll/confirm", ConfirmEnroll).WithName("PortalEnrollConfirm").WithOpenApi().RequireRateLimiting("portal-auth");
        session.MapPost("/logout", Logout).WithName("PortalLogout").WithOpenApi();
        session.MapGet("/me", Me).WithName("PortalMe").WithOpenApi();
        return app;
    }

    // ---- registration -----------------------------------------------------

    public sealed record RegisterRequest(
        [property: Required] string Email,
        [property: Required] string Password,
        [property: Required] string DisplayName,
        string? TurnstileToken);

    private static async Task<IResult> Register(
        [FromBody] RegisterRequest req, HttpContext http, IPortalAccountService service, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        var result = await service.RegisterAsync(
            new RegisterCommand(req.Email ?? string.Empty, req.Password ?? string.Empty, req.DisplayName ?? string.Empty, req.TurnstileToken),
            PortalRequest.Caller(http), ct);
        return result.Outcome switch
        {
            RegisterOutcome.Accepted => Results.Accepted(value: new { status = "accepted" }),
            RegisterOutcome.PortalDisabled => PortalRequest.Disabled(),
            RegisterOutcome.RegistrationDisabled => Results.Json(new { error = "registration_disabled", message = "Self-registration is not available. Ask your contact at the service desk for an invitation." }, statusCode: StatusCodes.Status403Forbidden),
            RegisterOutcome.InvalidEmail => Results.BadRequest(new { error = "invalid_email", message = "Enter a valid email address." }),
            RegisterOutcome.InvalidName => Results.BadRequest(new { error = "invalid_name", message = "Enter your name (2–120 characters)." }),
            RegisterOutcome.WeakPassword => Results.BadRequest(new { error = "weak_password", message = PasswordMessage(result.Detail) }),
            RegisterOutcome.TurnstileFailed => Results.BadRequest(new { error = "turnstile_failed", message = "The anti-bot check could not be verified. Please try again." }),
            RegisterOutcome.Misconfigured => Results.Json(new { error = "registration_unavailable", message = "Registration is temporarily unavailable. Please try again later." }, statusCode: StatusCodes.Status503ServiceUnavailable),
            RegisterOutcome.MailFailed => Results.Json(new { error = "mail_failed", message = "We could not send the confirmation mail. Please try again later." }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static string PasswordMessage(string? detail)
    {
        if (detail is not null && detail.StartsWith("password_too_short:", StringComparison.Ordinal))
            return $"Use at least {detail["password_too_short:".Length..]} characters.";
        return detail == "password_too_long" ? "The password is too long." : "Choose a stronger password.";
    }

    public sealed record TokenRequest([property: Required] string Token);

    private static async Task<IResult> VerifyEmail(
        [FromBody] TokenRequest req, HttpContext http, IPortalAccountService service, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        var result = await service.VerifyEmailAsync(req.Token ?? string.Empty, PortalRequest.Caller(http), ct);
        return result.Outcome switch
        {
            TokenOutcome.Ok => Results.Ok(new { status = result.AlreadyVerified ? "already_verified" : "verified" }),
            TokenOutcome.Expired => Results.Json(new { error = "expired", message = "This confirmation link has expired. Register again to receive a new one." }, statusCode: StatusCodes.Status410Gone),
            TokenOutcome.AlreadyUsed => Results.Ok(new { status = "already_verified" }),
            _ => Results.NotFound(new { error = "invalid", message = "This confirmation link is not valid." }),
        };
    }

    // ---- login ------------------------------------------------------------

    public sealed record LoginRequest([property: Required] string Email, [property: Required] string Password);

    private static async Task<IResult> Login(
        [FromBody] LoginRequest req, HttpContext http,
        IUserService users, IPasswordHasher hasher, ITotpService totp, ISessionService sessions,
        IPortalAccountRepository accounts, ISettingsService settings, IAuditLogger audit, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        var email = (req.Email ?? string.Empty).Trim();
        var password = req.Password ?? string.Empty;
        if (email.Length == 0 || password.Length == 0) return Results.Unauthorized();

        var ip = http.Connection.RemoteIpAddress?.ToString();
        var ua = http.Request.Headers.UserAgent.ToString();

        var user = await users.FindByEmailAsync(email, ct);
        // Same dummy-verify as the agent login so a miss costs the same time.
        if (user is null || user.RoleName != "Customer" || user.AuthMode != AuthModes.Local || string.IsNullOrEmpty(user.PasswordHash))
        {
            _ = hasher.Verify("$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", password, out _);
            await audit.LogAsync(new AuditEvent(PortalEventTypes.LoginFailed, email, "anon",
                Target: user?.Id.ToString(), ClientIp: ip, UserAgent: ua,
                Payload: new { reason = user is null ? "unknown_email" : "not_customer" }), ct);
            return Results.Unauthorized();
        }

        if (user.LockoutUntilUtc is { } until && until > DateTime.UtcNow)
        {
            await audit.LogAsync(new AuditEvent(PortalEventTypes.LoginLockedOut, user.Email, "Customer",
                Target: user.Id.ToString(), ClientIp: ip, UserAgent: ua), ct);
            return Results.StatusCode(StatusCodes.Status423Locked);
        }

        var verified = hasher.Verify(user.PasswordHash, password, out var rehash);
        if (!verified)
        {
            var maxAttempts = await settings.GetAsync<int>(SettingKeys.Security.LockoutMaxAttempts, ct);
            var windowSeconds = await settings.GetAsync<int>(SettingKeys.Security.LockoutWindowSeconds, ct);
            var lockoutSeconds = await settings.GetAsync<int>(SettingKeys.Security.LockoutDurationSeconds, ct);
            var locked = await users.RecordFailedLoginAsync(user.Id, maxAttempts, windowSeconds, lockoutSeconds, ct);
            await audit.LogAsync(new AuditEvent(locked ? PortalEventTypes.LoginLockedOut : PortalEventTypes.LoginFailed,
                user.Email, "Customer", Target: user.Id.ToString(), ClientIp: ip, UserAgent: ua,
                Payload: new { reason = "bad_password" }), ct);
            return locked ? Results.StatusCode(StatusCodes.Status423Locked) : Results.Unauthorized();
        }
        if (rehash) await users.UpdatePasswordHashAsync(user.Id, hasher.Hash(password), ct);

        // Password OK → the account state may be disclosed to its owner.
        var account = await accounts.GetByUserIdAsync(user.Id, ct);
        if (account is null || account.Status != PortalAccountStatus.Active || !user.IsActive)
        {
            var state = account?.Status ?? "Unknown";
            await audit.LogAsync(new AuditEvent(PortalEventTypes.LoginRefusedState, user.Email, "Customer",
                Target: user.Id.ToString(), ClientIp: ip, UserAgent: ua, Payload: new { state }), ct);
            return Results.Json(new { error = "account_state", state }, statusCode: StatusCodes.Status403Forbidden);
        }

        var enrolled = await totp.IsEnabledAsync(user.Id, ct);
        await users.RecordSuccessfulLoginAsync(user.Id, ct);

        // TOTP is mandatory: both "needs challenge" and "needs enrollment"
        // mint a PENDING session. RequireCustomer whitelists "pwd+mfa", so
        // nothing scoped is reachable until /2fa/verify or /2fa/enroll/confirm
        // upgrades the session.
        var lifetimeHours = Math.Max(1, await settings.GetAsync<int>(SettingKeys.Portal.SessionLifetimeHours, ct));
        await AuthEndpoints.EstablishSessionAsync(http, user, SessionAuthenticationHandler.AmrPending, sessions, settings,
            TimeSpan.FromHours(lifetimeHours), portalCookie: true, impersonatorUserId: null, ct);

        await audit.LogAsync(new AuditEvent(PortalEventTypes.LoginSuccess, user.Email, "Customer",
            Target: user.Id.ToString(), ClientIp: ip, UserAgent: ua,
            Payload: new { twoFactorChallengeRequired = enrolled, enrollmentRequired = !enrolled }), ct);

        return Results.Ok(new
        {
            email = user.Email,
            displayName = account.DisplayName,
            twoFactorRequired = enrolled,
            enrollmentRequired = !enrolled,
        });
    }

    // ---- TOTP challenge + forced enrollment -------------------------------

    public sealed record CodeRequest([property: Required] string Code);

    private static async Task<IResult> VerifyTwoFactor(
        [FromBody] CodeRequest req, HttpContext http, ITotpService totp, ISessionService sessions,
        IMemoryCache cache, IAuditLogger audit, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        if (!PortalRequest.IsCustomerPrincipal(http)) return Results.Unauthorized();
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var userId = PortalRequest.UserId(http);
        var sessionId = PortalRequest.SessionId(http);
        if (userId is null || sessionId is null) return Results.Unauthorized();

        var result = await totp.VerifyAsync(userId.Value, req.Code ?? string.Empty, ct);
        if (result == TwoFactorResult.Rejected)
        {
            await audit.LogAsync(new AuditEvent(PortalEventTypes.TwoFactorChallengeFailed,
                http.User.Identity?.Name ?? userId.ToString()!, "Customer", Target: userId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
            return Results.Unauthorized();
        }

        await sessions.UpgradeAmrAsync(sessionId.Value, SessionAuthenticationHandler.AmrPasswordPlusMfa, ct);
        cache.Remove(SessionAuthenticationHandler.CacheKey(sessionId.Value));
        await audit.LogAsync(new AuditEvent(PortalEventTypes.TwoFactorChallengeSuccess,
            http.User.Identity?.Name ?? userId.ToString()!, "Customer", Target: userId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { method = result == TwoFactorResult.RecoveryAccepted ? "recovery" : "totp" }), ct);
        return Results.Ok(new { ok = true, usedRecoveryCode = result == TwoFactorResult.RecoveryAccepted });
    }

    private static async Task<IResult> BeginEnroll(
        HttpContext http, ITotpService totp, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        if (!PortalRequest.IsCustomerPrincipal(http)) return Results.Unauthorized();
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var userId = PortalRequest.UserId(http);
        if (userId is null) return Results.Unauthorized();
        // Re-enrollment of an already enrolled account is an agent action
        // (TOTP reset), never self-service — a stolen pending session must
        // not be able to swap the authenticator.
        if (await totp.IsEnabledAsync(userId.Value, ct))
            return Results.Conflict(new { error = "already_enrolled" });
        var email = http.User.FindFirst(ClaimTypes.Email)?.Value ?? userId.Value.ToString();
        var enrollment = await totp.BeginEnrollAsync(userId.Value, email, ct);
        return Results.Ok(new { secret = enrollment.SecretBase32, otpauthUri = enrollment.OtpAuthUri });
    }

    private static async Task<IResult> ConfirmEnroll(
        [FromBody] CodeRequest req, HttpContext http, ITotpService totp, ISessionService sessions,
        IMemoryCache cache, IAuditLogger audit, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        if (!PortalRequest.IsCustomerPrincipal(http)) return Results.Unauthorized();
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var userId = PortalRequest.UserId(http);
        var sessionId = PortalRequest.SessionId(http);
        if (userId is null || sessionId is null) return Results.Unauthorized();
        if (await totp.IsEnabledAsync(userId.Value, ct))
            return Results.Conflict(new { error = "already_enrolled" });

        var codes = await totp.ConfirmEnrollAsync(userId.Value, req.Code ?? string.Empty, ct);
        if (codes is null) return Results.BadRequest(new { error = "invalid_code", message = "The code is not valid. Check your authenticator app and try again." });

        await sessions.UpgradeAmrAsync(sessionId.Value, SessionAuthenticationHandler.AmrPasswordPlusMfa, ct);
        cache.Remove(SessionAuthenticationHandler.CacheKey(sessionId.Value));
        await audit.LogAsync(new AuditEvent(PortalEventTypes.TwoFactorEnrolled,
            http.User.Identity?.Name ?? userId.ToString()!, "Customer", Target: userId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Ok(new { recoveryCodes = codes });
    }

    // ---- logout + me ------------------------------------------------------

    private static async Task<IResult> Logout(
        HttpContext http, ISessionService sessions, ISettingsService settings, IUserService users,
        IMemoryCache cache, IAuditLogger audit, CancellationToken ct)
    {
        var cookieName = await settings.GetAsync<string>(SettingKeys.Security.PortalSessionCookieName, ct);
        if (Guid.TryParse(http.Request.Cookies[cookieName], out var sessionId))
        {
            await sessions.RevokeAsync(sessionId, ct);
            // Evict the handler's 5-minute cache so the revocation bites
            // immediately — for a shadow session "Exit" must be instant.
            cache.Remove(SessionAuthenticationHandler.CacheKey(sessionId));
            if (PortalRequest.IsImpersonated(http))
            {
                var impersonatorId = PortalRequest.ImpersonatorId(http);
                var impersonator = impersonatorId is { } iid ? await users.FindByIdAsync(iid, ct) : null;
                await audit.LogAsync(new AuditEvent(PortalEventTypes.ImpersonationEnded,
                    impersonator?.Email ?? impersonatorId?.ToString() ?? "unknown", "Admin",
                    Target: PortalRequest.UserId(http)?.ToString(),
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { sessionId, customerEmail = http.User.Identity?.Name }), ct);
            }
            else
            {
                await audit.LogAsync(new AuditEvent(PortalEventTypes.Logout,
                    http.User.Identity?.Name ?? "anon", http.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
                    Target: sessionId.ToString(), ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
            }
        }
        AuthEndpoints.ClearAuthCookies(http, cookieName, portalCookie: true);
        return Results.Ok();
    }

    private static async Task<IResult> Me(
        HttpContext http, IPortalAccountRepository accounts, ITotpService totp, ISettingsService settings, CancellationToken ct)
    {
        var enabled = await PortalRequest.PortalEnabledAsync(settings, ct);
        if (!enabled || !PortalRequest.IsCustomerPrincipal(http))
            return Results.Ok(new { enabled, user = (object?)null, serverTimeUtc = DateTimeOffset.UtcNow });
        var userId = PortalRequest.UserId(http);
        if (userId is null) return Results.Ok(new { enabled, user = (object?)null, serverTimeUtc = DateTimeOffset.UtcNow });

        var viewer = await accounts.GetViewerAsync(userId.Value, ct);
        if (viewer is null || viewer.Status != PortalAccountStatus.Active)
            return Results.Ok(new { enabled, user = (object?)null, serverTimeUtc = DateTimeOffset.UtcNow });

        // Re-mint the portal CSRF cookie when a live session lost it (see
        // AuthEndpoints.EnsureCsrfCookie) — e.g. a session predating the
        // per-realm cookie split.
        AuthEndpoints.EnsureCsrfCookie(http, portalCookie: true);

        var amr = http.User.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value ?? string.Empty;
        var enrolled = await totp.IsEnabledAsync(userId.Value, ct);
        var contactName = $"{viewer.ContactFirstName} {viewer.ContactLastName}".Trim();
        return Results.Ok(new
        {
            enabled,
            user = new
            {
                id = viewer.UserId,
                email = viewer.Email,
                displayName = string.IsNullOrWhiteSpace(viewer.DisplayName) ? contactName : viewer.DisplayName,
                amr,
                impersonated = amr == SessionAuthenticationHandler.AmrImpersonated,
                twoFactorEnrolled = enrolled,
                // Companies the customer may act in, each with its portal
                // role; the primary link first. The portal header switches
                // between them; the list only ever shows one at a time.
                companies = viewer.Companies.Select(c => new
                {
                    id = c.CompanyId,
                    name = c.CompanyName,
                    role = c.Role,
                    isPrimary = c.IsPrimary,
                    canSeeCompanyTickets = c.IsTicketManager,
                }),
            },
            serverTimeUtc = DateTimeOffset.UtcNow,
        });
    }

    // ---- password reset ---------------------------------------------------

    public sealed record ForgotPasswordRequest([property: Required] string Email);

    private static async Task<IResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest req, HttpContext http, IPortalAccountService service, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        await service.ForgotPasswordAsync(req.Email ?? string.Empty, PortalRequest.Caller(http), ct);
        // Always the same answer — no account enumeration.
        return Results.Accepted(value: new { status = "accepted" });
    }

    public sealed record ResetPasswordRequest([property: Required] string Token, [property: Required] string Password);

    private static async Task<IResult> ResetPassword(
        [FromBody] ResetPasswordRequest req, HttpContext http, IPortalAccountService service, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        var outcome = await service.ResetPasswordAsync(req.Token ?? string.Empty, req.Password ?? string.Empty, PortalRequest.Caller(http), ct);
        return outcome switch
        {
            ResetPasswordOutcome.Done => Results.Ok(new { status = "done" }),
            ResetPasswordOutcome.Expired => Results.Json(new { error = "expired", message = "This reset link has expired. Request a new one." }, statusCode: StatusCodes.Status410Gone),
            ResetPasswordOutcome.WeakPassword => Results.BadRequest(new { error = "weak_password", message = PasswordMessage(await service.ValidatePasswordAsync(req.Password ?? string.Empty, ct)) }),
            _ => Results.NotFound(new { error = "invalid", message = "This reset link is not valid." }),
        };
    }

    // ---- invitations ------------------------------------------------------

    private static async Task<IResult> DescribeInvitation(
        [FromQuery] string? token, IPortalAccountService service, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        var info = await service.DescribeInvitationAsync(token ?? string.Empty, ct);
        return info.Outcome switch
        {
            TokenOutcome.Ok => Results.Ok(new { email = info.Email, displayName = info.DisplayName, companyName = info.CompanyName, companyNames = info.CompanyNames ?? Array.Empty<string>() }),
            TokenOutcome.Expired => Results.Json(new { error = "expired", message = "This invitation has expired. Ask the service desk to send a new one." }, statusCode: StatusCodes.Status410Gone),
            TokenOutcome.AlreadyUsed => Results.Conflict(new { error = "already_used", message = "This invitation was already used. Sign in instead." }),
            _ => Results.NotFound(new { error = "invalid", message = "This invitation link is not valid." }),
        };
    }

    public sealed record AcceptInvitationRequest([property: Required] string Token, [property: Required] string Password);

    private static async Task<IResult> AcceptInvitation(
        [FromBody] AcceptInvitationRequest req, HttpContext http, IPortalAccountService service, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return PortalRequest.Disabled();
        var result = await service.AcceptInvitationAsync(req.Token ?? string.Empty, req.Password ?? string.Empty, PortalRequest.Caller(http), ct);
        return result.Outcome switch
        {
            AcceptInviteOutcome.Created => Results.Ok(new { status = "created", email = result.Email }),
            AcceptInviteOutcome.Expired => Results.Json(new { error = "expired", message = "This invitation has expired. Ask the service desk to send a new one." }, statusCode: StatusCodes.Status410Gone),
            AcceptInviteOutcome.EmailTaken => Results.Conflict(new { error = "email_taken", message = "An account with this email address already exists. Sign in or reset your password." }),
            AcceptInviteOutcome.ContactHasAccount => Results.Conflict(new { error = "contact_has_account", message = "A portal account already exists for this contact. Sign in or reset your password." }),
            AcceptInviteOutcome.WeakPassword => Results.BadRequest(new { error = "weak_password", message = PasswordMessage(await service.ValidatePasswordAsync(req.Password ?? string.Empty, ct)) }),
            _ => Results.NotFound(new { error = "invalid", message = "This invitation link is not valid." }),
        };
    }
}
