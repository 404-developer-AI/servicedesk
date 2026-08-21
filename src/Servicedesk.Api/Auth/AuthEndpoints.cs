using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Secrets;
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

        // Deliberately WITHOUT the RequireAgent policy (v0.1.3): when
        // Security.TwoFactor.Required is on, an un-enrolled staff login mints
        // an "mfa-pending" session that the role policies reject — but that
        // session must be able to reach exactly these two endpoints to
        // enroll. Each handler checks the principal itself (authenticated +
        // staff role), mirroring the portal's forced-enrollment endpoints.
        group.MapPost("/2fa/enroll/begin", BeginTotpEnroll)
            .WithName("AuthTotpBegin")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapPost("/2fa/enroll/confirm", ConfirmTotpEnroll)
            .WithName("AuthTotpConfirm")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapPost("/2fa/disable", DisableTotp)
            .WithName("AuthTotpDisable")
            .WithOpenApi()
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireRateLimiting("auth");

        // v0.1.3 — self-service password change for Local staff accounts
        // (audit v0.1.1 #8). Requires the current password, enforces the
        // minimum length, revokes every OTHER session and is audited.
        group.MapPost("/change-password", ChangePassword)
            .WithName("AuthChangePassword")
            .WithOpenApi()
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireRateLimiting("auth");

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
        // v0.1.0 — lets the staff login page link to the customer portal
        // when it is switched on (a boolean only; the portal has its own
        // public config endpoint).
        var portalEnabled = await settings.GetAsync<bool>(SettingKeys.Portal.Enabled, ct);
        return Results.Ok(new
        {
            microsoftEnabled,
            setupAvailable = userCount == 0,
            portalEnabled,
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

    public sealed record LoginResponse(string Email, string Role, bool TwoFactorRequired, bool EnrollmentRequired);

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
        // v0.1.0 — customer accounts authenticate through the portal flow
        // (/api/portal/auth/login, mandatory TOTP). The agent login never
        // mints a session for them; same generic 401 as a wrong password.
        var isCustomer = string.Equals(user.RoleName, "Customer", StringComparison.Ordinal);
        if (isCustomer || user.AuthMode != AuthModes.Local || string.IsNullOrEmpty(user.PasswordHash) || !user.IsActive)
        {
            _ = hasher.Verify("$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", request.Password, out _);
            await audit.LogAsync(new AuditEvent(
                EventType: AuthEventTypes.LoginFailed,
                Actor: user.Email,
                ActorRole: user.RoleName,
                Target: user.Id.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = isCustomer ? "customer_use_portal" : user.IsActive ? "wrong_channel" : "inactive" }), ct);
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
        // v0.1.3 — Security.TwoFactor.Required is now enforced (audit v0.1.1
        // #1): with the flag on, a Local staff user who never enrolled gets a
        // PENDING session that can only reach the /2fa/enroll endpoints, the
        // same forced-enrollment model the customer portal has always had.
        // M365 sign-ins are unaffected — their MFA lives in Entra policies.
        var twoFactorRequired = await settings.GetAsync<bool>(SettingKeys.Security.TwoFactorRequired, ct);
        var enrollmentRequired = twoFactorRequired && !twoFactorEnabled;
        await users.RecordSuccessfulLoginAsync(user.Id, ct);

        // 2FA-enabled users get a PENDING session (password step done, TOTP
        // still owed). It authenticates but the role policies reject it until
        // /2fa/verify upgrades it to "pwd+mfa" — so the cookie alone is useless
        // to a client that skips the challenge. Non-2FA users are fully
        // authorized immediately ("pwd") unless enrollment is being forced.
        var amr = twoFactorEnabled || enrollmentRequired
            ? SessionAuthenticationHandler.AmrPending
            : AmrPassword;
        await EstablishSessionAsync(httpContext, user, amr, sessions, settings, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.LoginSuccess,
            Actor: user.Email,
            ActorRole: user.RoleName,
            Target: user.Id.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
            Payload: new { twoFactorChallengeRequired = twoFactorEnabled, enrollmentRequired }), ct);

        return Results.Ok(new LoginResponse(user.Email, user.RoleName, twoFactorEnabled, enrollmentRequired));
    }

    public sealed record VerifyTwoFactorRequest([property: Required] string Code);

    private static async Task<IResult> VerifyTwoFactor(
        [FromBody] VerifyTwoFactorRequest request,
        HttpContext httpContext,
        ITotpService totp,
        ISessionService sessions,
        ISettingsService settings,
        IUserService users,
        IMemoryCache cache,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var sidClaim = httpContext.User.FindFirst("sid")?.Value;
        var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sidClaim, out var sessionId) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        // v0.1.3 (audit v0.1.1 #6) — the account lockout now guards the second
        // factor exactly like the password step: a locked account cannot burn
        // through codes, and every rejected code feeds the same counter below.
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Results.Unauthorized();
        if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc > DateTimeOffset.UtcNow)
        {
            return Results.StatusCode(StatusCodes.Status423Locked);
        }

        var result = await totp.VerifyAsync(userId, request.Code ?? string.Empty, ct);
        if (result == TwoFactorResult.Rejected)
        {
            var nowLocked = await RecordTwoFactorFailureAsync(users, settings, userId, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: nowLocked ? AuthEventTypes.LoginLockedOut : AuthEventTypes.TwoFactorChallengeFailed,
                Actor: httpContext.User.Identity?.Name ?? userId.ToString(),
                ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
                Target: userId.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { step = "2fa" }), ct);
            return nowLocked
                ? Results.StatusCode(StatusCodes.Status423Locked)
                : Results.Unauthorized();
        }

        // Successful challenge clears the streak the failed codes built up.
        await users.RecordSuccessfulLoginAsync(userId, ct);

        await sessions.UpgradeAmrAsync(sessionId, AmrPasswordPlusMfa, ct);
        // Evict the handler's cached (pre-upgrade) session so the new
        // "pwd+mfa" amr takes effect on the very next request instead of after
        // the cache window — otherwise the user stays blocked right
        // after a successful challenge.
        cache.Remove(SessionAuthenticationHandler.CacheKey(sessionId));

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
        IUserService users,
        Servicedesk.Infrastructure.Dashboard.IDashboardTilesService dashboardTiles,
        ISettingsService settings,
        CancellationToken ct)
    {
        var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Ok(new { user = (object?)null, serverTimeUtc = DateTimeOffset.UtcNow });
        }
        // Re-mint the CSRF cookie when a live session lost it (see
        // EnsureCsrfCookie) — without this, sign-out itself is unreachable.
        EnsureCsrfCookie(httpContext);
        var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var role = httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        var amr = httpContext.User.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value ?? AmrPassword;
        var twoFactorEnabled = await totp.IsEnabledAsync(userId, ct);

        // v0.0.35 — surface the Timesheet feature flags so the frontend
        // can decide whether to render the menu item without a second
        // round-trip. Lives on IUserService so the test fakes don't
        // need a real NpgsqlDataSource for the auth-endpoint surface.
        var flags = await users.GetTimesheetFlagsAsync(userId, ct);
        // v0.0.40 — same pattern for the ISO 27001 workflow flags. The
        // ticket-detail page reads these to decide which classification
        // buttons to render.
        var isoFlags = await users.GetIsoFlagsAsync(userId, ct);
        // v0.0.40 polish — KB access is per-user opt-in. Sidebar + Settings
        // nav rail both gate on this flag.
        var kbEnabled = await users.GetKbEnabledAsync(userId, ct);
        // Sidebar feature flag. Default true on missing rows so a
        // session that outlives a deleted user is harmless.
        var searchEnabled = await users.GetSearchEnabledAsync(userId, ct);
        // v0.0.42 — per-user opt-in for the agent activity feed. Drives
        // the dashboard tile + /admin/activity nav visibility and the
        // SignalR hub's group enrollment.
        var activityFeedEnabled = await users.GetActivityFeedEnabledAsync(userId, ct);
        // v0.0.52 — per-user opt-in for the Assets page (Tactical RMM
        // mirror). Drives the sidebar nav entry. Backend /api/assets
        // routes carry RequireAgent so the gate is enforced on both ends.
        var assetsEnabled = await users.GetAssetsEnabledAsync(userId, ct);
        // Per-user opt-in for the Adsolut timesheet tab. Paired below with
        // the live Adsolut connection state — the tab only renders when
        // both are true.
        var adsolutTimesheetEnabled = await users.GetAdsolutTimesheetEnabledAsync(userId, ct);
        // v0.0.59 — per-user opt-in for the Adsolut Orders feature (navbar
        // overview under Assets, ticket "Sync orders" button, "::" order
        // linking). Paired below with the live Adsolut connection state — the
        // overview only renders when both are true.
        var adsolutOrdersEnabled = await users.GetAdsolutOrdersEnabledAsync(userId, ct);
        // v0.0.56 — per-user opt-in for the back-office Resolved + CWI
        // timesheet tabs. Gates the two tabs in the SPA; the underlying
        // /api/timesheet/backoffice endpoints carry RequireAgent so the
        // gate is feature-visibility, not security authorization.
        var timesheetBackofficeEnabled = await users.GetTimesheetBackofficeEnabledAsync(userId, ct);
        // v0.0.69 — per-user opt-in for the Statistics feature. Read gates the
        // page + its assigned tiles; write gates the tile-builder. The
        // underlying /api/statistics endpoints enforce the same flags so this
        // is feature-visibility, not the security boundary.
        var statisticsRead = await users.GetStatisticsReadEnabledAsync(userId, ct);
        var statisticsWrite = await users.GetStatisticsWriteEnabledAsync(userId, ct);
        // v0.0.76 — per-user opt-in for the Contracts page (tile hub; the
        // contract data model lands later). Drives the sidebar nav entry.
        var contractsEnabled = await users.GetContractsEnabledAsync(userId, ct);
        // Per-user opt-in for the Employee Feedback board. Both flags drive the
        // sidebar nav entry + the /feedback route gate; the /api/feedback/*
        // endpoints enforce the resolved access scope. feedbackEnabled = full
        // access (shared board); feedbackOwnOnly = restricted (log + see own).
        var feedbackAccess = await users.GetFeedbackAccessAsync(userId, ct);
        var feedbackEnabled = feedbackAccess.Enabled;
        var feedbackOwnOnly = feedbackAccess.OwnOnly;
        // Whether the Adsolut integration is connected (configured + valid
        // refresh token, no refresh error). Resolved here so a non-admin
        // agent can gate the Adsolut timesheet tab without the admin-only
        // integrations status endpoint. The stores are pulled off
        // RequestServices so test fixtures that don't register the Adsolut
        // surface still build the endpoint; any failure floors to false.
        var adsolutConnected = await ResolveAdsolutConnectedAsync(httpContext, settings, ct);
        // Per-user Dashboard tile preferences. Empty array = no tiles
        // enabled; DashboardPage renders an empty-state in that case.
        // Shape: ordered [{tileId, size}] so the frontend can render the
        // layout without a second round-trip. v0.0.42 added position +
        // size — the order returned here is the saved order.
        var tiles = await dashboardTiles.GetForUserAsync(userId, ct);

        // v0.0.44 — Effective theme (steaan | light | dark, v0.0.108)
        // resolved server-side so the first paint after login agrees with
        // the saved preference (or the admin default when the user has not
        // yet picked). Cascade: user pref → Ui.DefaultTheme → UiThemes.Factory. The NpgsqlDataSource is
        // resolved lazily off `RequestServices` so test fixtures that
        // don't register it (the anonymous-/me round-trip never reaches
        // this branch) still build the endpoint without a DI failure.
        var dataSource = httpContext.RequestServices.GetService<Npgsql.NpgsqlDataSource>();
        var effectiveTheme = await ResolveEffectiveThemeAsync(userId, dataSource, settings, ct);

        return Results.Ok(new
        {
            user = new
            {
                id = userId,
                email,
                role,
                amr,
                twoFactorEnabled,
                timesheetEnabled = flags.Enabled,
                timesheetManager = flags.Manager,
                isIsoMgm = isoFlags.Mgm,
                isIsoDpo = isoFlags.Dpo,
                kbEnabled,
                searchEnabled,
                activityFeedEnabled,
                assetsEnabled,
                adsolutTimesheetEnabled,
                timesheetBackofficeEnabled,
                adsolutOrdersEnabled,
                statisticsRead,
                statisticsWrite,
                contractsEnabled,
                feedbackEnabled,
                feedbackOwnOnly,
                adsolutConnected,
                dashboardTiles = tiles.Select(t => new { tileId = t.TileId, size = t.Size }).ToList(),
                effectiveTheme,
            },
            serverTimeUtc = DateTimeOffset.UtcNow,
        });
    }

    /// True only when the Adsolut integration is fully connected (client
    /// configured + valid refresh token + no refresh error). Reuses the
    /// single source of truth in <see cref="AdsolutStateResolver"/> so this
    /// can never drift from the admin status tile. The two stores are
    /// resolved off RequestServices — when either is absent (test
    /// fixtures, or a build without the Adsolut surface registered) we
    /// floor to false rather than failing the whole /auth/me call.
    private static async Task<bool> ResolveAdsolutConnectedAsync(
        HttpContext httpContext,
        ISettingsService settings,
        CancellationToken ct)
    {
        try
        {
            var secrets = httpContext.RequestServices.GetService<IProtectedSecretStore>();
            var connections = httpContext.RequestServices.GetService<IAdsolutConnectionStore>();
            if (secrets is null || connections is null) return false;

            var state = await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, ct);
            return string.Equals(state, AdsolutStateResolver.Connected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ResolveEffectiveThemeAsync(
        Guid userId,
        Npgsql.NpgsqlDataSource? dataSource,
        ISettingsService settings,
        CancellationToken ct)
    {
        try
        {
            // Tests can run without a real Postgres registered — in that
            // case the per-user override lookup is skipped and we fall
            // straight through to the admin default → factory floor.
            if (dataSource is not null)
            {
                await using var conn = await dataSource.OpenConnectionAsync(ct);
                var raw = await Dapper.SqlMapper.ExecuteScalarAsync<string?>(conn, new Dapper.CommandDefinition(
                    "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = 'ui:theme'",
                    new { userId }, cancellationToken: ct));
                var user = UiThemes.Normalize(raw);
                if (user is not null) return user;
            }

            return UiThemes.NormalizeOrFactory(await settings.GetAsync<string>(SettingKeys.Ui.DefaultTheme, ct));
        }
        catch
        {
            return UiThemes.Factory;
        }
    }

    // ---- TOTP enrollment ---------------------------------------------------

    private static async Task<IResult> BeginTotpEnroll(
        HttpContext httpContext,
        ITotpService totp,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (!IsStaffPrincipal(httpContext)) return Results.Unauthorized();
        var userId = RequireUserId(httpContext);
        if (userId is null) return Results.Unauthorized();
        // Re-enrollment guard (audit v0.1.1 #7): BeginEnroll upserts a fresh
        // secret with enabled=FALSE, so calling it against a working TOTP
        // setup would silently disable it. An enrolled account must first go
        // through /2fa/disable (which demands a valid current code).
        if (await totp.IsEnabledAsync(userId.Value, ct))
        {
            return Results.Conflict(new { error = "already_enrolled" });
        }
        var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value ?? userId.Value.ToString();
        var enrollment = await totp.BeginEnrollAsync(userId.Value, email, ct);
        // Audited: this call rotates the pending secret, which is the state
        // transition that matters if enrollment is later abandoned.
        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.TwoFactorEnrollStarted,
            Actor: httpContext.User.Identity?.Name ?? userId.Value.ToString(),
            ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
            Target: userId.Value.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);
        return Results.Ok(new { secret = enrollment.SecretBase32, otpauthUri = enrollment.OtpAuthUri });
    }

    public sealed record ConfirmTotpRequest([property: Required] string Code);

    private static async Task<IResult> ConfirmTotpEnroll(
        [FromBody] ConfirmTotpRequest request,
        HttpContext httpContext,
        ITotpService totp,
        ISessionService sessions,
        IMemoryCache cache,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (!IsStaffPrincipal(httpContext)) return Results.Unauthorized();
        var userId = RequireUserId(httpContext);
        if (userId is null) return Results.Unauthorized();
        if (await totp.IsEnabledAsync(userId.Value, ct))
        {
            return Results.Conflict(new { error = "already_enrolled" });
        }
        var codes = await totp.ConfirmEnrollAsync(userId.Value, request.Code ?? string.Empty, ct);
        if (codes is null)
        {
            return Results.BadRequest(new { error = "Invalid verification code." });
        }

        var sidClaim = httpContext.User.FindFirst("sid")?.Value;
        if (Guid.TryParse(sidClaim, out var sessionId))
        {
            await sessions.UpgradeAmrAsync(sessionId, AmrPasswordPlusMfa, ct);
            // Refresh the cached session so /me reflects the new amr at once.
            cache.Remove(SessionAuthenticationHandler.CacheKey(sessionId));
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

    public sealed record DisableTotpRequest([property: Required] string Code);

    private static async Task<IResult> DisableTotp(
        [FromBody] DisableTotpRequest request,
        HttpContext httpContext,
        ITotpService totp,
        IUserService users,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null) return Results.Unauthorized();

        // Step-up (audit v0.1.1 #7): removing the second factor demands proof
        // of the second factor — a live TOTP code or a recovery code. A
        // stolen session cookie alone can no longer downgrade the account.
        // Bad codes feed the same lockout counter as the login challenge.
        var user = await users.FindByIdAsync(userId.Value, ct);
        if (user is null) return Results.Unauthorized();
        if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc > DateTimeOffset.UtcNow)
        {
            return Results.StatusCode(StatusCodes.Status423Locked);
        }

        var result = await totp.VerifyAsync(userId.Value, request.Code ?? string.Empty, ct);
        if (result == TwoFactorResult.Rejected)
        {
            var nowLocked = await RecordTwoFactorFailureAsync(users, settings, userId.Value, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: nowLocked ? AuthEventTypes.LoginLockedOut : AuthEventTypes.TwoFactorChallengeFailed,
                Actor: httpContext.User.Identity?.Name ?? userId.Value.ToString(),
                ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
                Target: userId.Value.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { step = "2fa_disable" }), ct);
            return nowLocked
                ? Results.StatusCode(StatusCodes.Status423Locked)
                : Results.BadRequest(new { error = "invalid_code" });
        }

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

    // ---- Password change (v0.1.3) ------------------------------------------

    public sealed record ChangePasswordRequest(
        [property: Required] string CurrentPassword,
        [property: Required] string NewPassword);

    private static async Task<IResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        HttpContext httpContext,
        IUserService users,
        IPasswordHasher hasher,
        ISessionService sessions,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var userId = RequireUserId(httpContext);
        var sidClaim = httpContext.User.FindFirst("sid")?.Value;
        if (userId is null || !Guid.TryParse(sidClaim, out var sessionId)) return Results.Unauthorized();

        var user = await users.FindByIdAsync(userId.Value, ct);
        if (user is null) return Results.Unauthorized();
        if (user.AuthMode != AuthModes.Local || string.IsNullOrEmpty(user.PasswordHash))
        {
            return Results.BadRequest(new { error = "Microsoft accounts change their password at Microsoft." });
        }
        if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc > DateTimeOffset.UtcNow)
        {
            return Results.StatusCode(StatusCodes.Status423Locked);
        }

        if (!hasher.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty, out _))
        {
            // Wrong current password on a live session is the same signal as
            // a wrong password at login — feed the same lockout counter.
            var nowLocked = await RecordTwoFactorFailureAsync(users, settings, userId.Value, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: nowLocked ? AuthEventTypes.LoginLockedOut : AuthEventTypes.LoginFailed,
                Actor: user.Email,
                ActorRole: user.RoleName,
                Target: user.Id.ToString(),
                ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
                Payload: new { step = "change_password" }), ct);
            return nowLocked
                ? Results.StatusCode(StatusCodes.Status423Locked)
                : Results.BadRequest(new { error = "invalid_current_password" });
        }

        var minLength = await settings.GetAsync<int>(SettingKeys.Security.PasswordMinimumLength, ct);
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < minLength)
        {
            return Results.BadRequest(new { error = $"Password must be at least {minLength} characters." });
        }

        await users.UpdatePasswordHashAsync(userId.Value, hasher.Hash(request.NewPassword), ct);
        // "Log everything else out": any other session — a stolen one
        // included — dies now; the session doing the change stays.
        await sessions.RevokeAllForUserExceptAsync(userId.Value, sessionId, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: AuthEventTypes.PasswordChanged,
            Actor: user.Email,
            ActorRole: user.RoleName,
            Target: user.Id.ToString(),
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString()), ct);
        return Results.Ok();
    }

    // ---- Helpers -----------------------------------------------------------

    /// True for an authenticated staff principal regardless of its amr — the
    /// forced-enrollment endpoints must accept "mfa-pending" sessions, but
    /// never a customer (customers enroll through /api/portal/auth).
    private static bool IsStaffPrincipal(HttpContext httpContext) =>
        httpContext.User.Identity?.IsAuthenticated == true
        && httpContext.User.FindFirst(ClaimTypes.Role)?.Value is "Agent" or "Admin";

    /// Feeds a failed second-factor (or step-up) attempt into the same
    /// lockout counter the password step uses, with the same settings.
    private static async Task<bool> RecordTwoFactorFailureAsync(
        IUserService users, ISettingsService settings, Guid userId, CancellationToken ct)
    {
        var maxAttempts = await settings.GetAsync<int>(SettingKeys.Security.LockoutMaxAttempts, ct);
        var windowSeconds = await settings.GetAsync<int>(SettingKeys.Security.LockoutWindowSeconds, ct);
        var durationSeconds = await settings.GetAsync<int>(SettingKeys.Security.LockoutDurationSeconds, ct);
        return await users.RecordFailedLoginAsync(userId, maxAttempts, windowSeconds, durationSeconds, ct);
    }

    private static Guid? RequireUserId(HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static Task EstablishSessionAsync(
        HttpContext httpContext,
        ApplicationUser user,
        string amr,
        ISessionService sessions,
        ISettingsService settings,
        CancellationToken ct)
        => EstablishSessionAsync(httpContext, user, amr, sessions, settings, lifetimeOverride: null, ct);

    /// Mints a session + the cookie pair. Shared with the customer-portal
    /// login (v0.1.0), which passes its own lifetime (Portal.SessionLifetimeHours)
    /// so the cookie semantics stay identical across both flows.
    internal static Task<Guid> EstablishSessionAsync(
        HttpContext httpContext,
        ApplicationUser user,
        string amr,
        ISessionService sessions,
        ISettingsService settings,
        TimeSpan? lifetimeOverride,
        CancellationToken ct)
        => EstablishSessionAsync(httpContext, user, amr, sessions, settings, lifetimeOverride,
            portalCookie: false, impersonatorUserId: null, ct);

    /// Full form (v0.1.1): <paramref name="portalCookie"/> writes the
    /// customer-portal session cookie instead of the staff one (the portal
    /// rides its own cookie so both sessions can coexist in one browser);
    /// <paramref name="impersonatorUserId"/> marks an admin's read-only
    /// shadow session and is recorded on the session row.
    internal static async Task<Guid> EstablishSessionAsync(
        HttpContext httpContext,
        ApplicationUser user,
        string amr,
        ISessionService sessions,
        ISettingsService settings,
        TimeSpan? lifetimeOverride,
        bool portalCookie,
        Guid? impersonatorUserId,
        CancellationToken ct)
    {
        var lifetime = lifetimeOverride
            ?? TimeSpan.FromHours(await settings.GetAsync<int>(SettingKeys.Security.SessionLifetimeHours, ct));
        var cookieName = await settings.GetAsync<string>(
            portalCookie ? SettingKeys.Security.PortalSessionCookieName : SettingKeys.Security.SessionCookieName, ct);
        var sessionId = await sessions.CreateAsync(
            user.Id,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            lifetime,
            amr,
            impersonatorUserId,
            ct);

        var secure = !httpContext.Request.IsHttps ? false : true;
        var expires = DateTimeOffset.UtcNow.Add(lifetime);

        httpContext.Response.Cookies.Append(cookieName, sessionId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
        });

        // v0.1.1 — the CSRF cookie follows the session-cookie split: the portal
        // gets its own so the two realms can coexist in one browser without
        // one flow's logout deleting the other's token.
        var csrfCookieName = portalCookie
            ? DoubleSubmitCsrfMiddleware.PortalCookieName
            : DoubleSubmitCsrfMiddleware.CookieName;
        var csrfToken = DoubleSubmitCsrfMiddleware.GenerateToken();
        httpContext.Response.Cookies.Append(csrfCookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
        });
        return sessionId;
    }

    /// Self-healing for the double-submit pair (v0.1.1): a live session whose
    /// CSRF cookie is gone — wiped by the pre-split shared-cookie bug, or by a
    /// selective cookie clear — would be stuck: every write 403s, including
    /// logout itself. `/me` calls this so an authenticated GET re-mints the
    /// missing token for its own realm. Safe because double-submit stores
    /// nothing server-side; the token only has to match its own header copy.
    internal static void EnsureCsrfCookie(HttpContext httpContext, bool portalCookie = false)
    {
        var csrfCookieName = portalCookie
            ? DoubleSubmitCsrfMiddleware.PortalCookieName
            : DoubleSubmitCsrfMiddleware.CookieName;
        if (!string.IsNullOrEmpty(httpContext.Request.Cookies[csrfCookieName])) return;
        httpContext.Response.Cookies.Append(csrfCookieName, DoubleSubmitCsrfMiddleware.GenerateToken(), new CookieOptions
        {
            HttpOnly = false,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            // Session-scoped: the next /me mints a fresh one when needed, so
            // it never has to outlive the session cookie it accompanies.
        });
    }

    /// Clears one realm's cookie pair. <paramref name="portalCookie"/> selects
    /// the customer-portal CSRF cookie; a portal logout must never delete the
    /// staff token (or vice versa) because both sessions can be live in the
    /// same browser — an admin running a shadow view is exactly that case.
    internal static void ClearAuthCookies(HttpContext httpContext, string sessionCookieName, bool portalCookie = false)
    {
        var csrfCookieName = portalCookie
            ? DoubleSubmitCsrfMiddleware.PortalCookieName
            : DoubleSubmitCsrfMiddleware.CookieName;
        httpContext.Response.Cookies.Delete(sessionCookieName, new CookieOptions { Path = "/" });
        httpContext.Response.Cookies.Delete(csrfCookieName, new CookieOptions { Path = "/" });
    }
}
