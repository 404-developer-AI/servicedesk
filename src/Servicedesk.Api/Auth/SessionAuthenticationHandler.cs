using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Auth;

/// Reads the opaque session cookie, validates it against
/// <see cref="ISessionService"/>, and hydrates a <see cref="ClaimsPrincipal"/>
/// with subject, email, role, and amr claims. Zero JWT, no Identity cookie
/// middleware — just a thin wrapper so authorization policies and
/// <c>RequireAuthorization</c> work as usual.
///
/// Validated sessions are cached in-memory for up to 5 minutes to avoid
/// hitting the database on every single request. The touch (last_seen_utc
/// update) is throttled to at most once per 60 seconds per session.
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Servicedesk.Session";
    public const string AmrClaimType = "amr";

    /// How long a validated session is kept in the memory cache before
    /// we re-query the database.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// Minimum interval between touch (last_seen_utc) writes for the same
    /// session. Must be well below the idle timeout (default 60 min).
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(60);

    private readonly ISessionService _sessions;
    private readonly ISettingsService _settings;
    private readonly IMemoryCache _cache;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionService sessions,
        ISettingsService settings,
        IMemoryCache cache)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _settings = settings;
        _cache = cache;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieName = await _settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var cookieValue = Request.Cookies[cookieName];
        if (string.IsNullOrWhiteSpace(cookieValue) || !Guid.TryParse(cookieValue, out var sessionId))
        {
            return AuthenticateResult.NoResult();
        }

        var cacheKey = $"session:{sessionId}";

        // Try the in-memory cache first to avoid a DB round-trip.
        if (!_cache.TryGetValue(cacheKey, out CachedSession? cached) || cached is null)
        {
            var idleMinutes = await _settings.GetAsync<int>(SettingKeys.Security.SessionIdleTimeoutMinutes);
            var validation = await _sessions.ValidateAsync(sessionId, TimeSpan.FromMinutes(idleMinutes), Context.RequestAborted);
            if (validation is null)
            {
                _cache.Remove(cacheKey);
                return AuthenticateResult.NoResult();
            }

            cached = new CachedSession(validation, DateTime.UtcNow);
            _cache.Set(cacheKey, cached, CacheDuration);
        }

        // Throttle touch: only write last_seen_utc if enough time has passed.
        if (DateTime.UtcNow - cached.LastTouchedUtc > TouchThrottle)
        {
            cached = cached with { LastTouchedUtc = DateTime.UtcNow };
            _cache.Set(cacheKey, cached, CacheDuration);
            _ = _sessions.TouchAsync(sessionId, Context.RequestAborted);
        }

        var v = cached.Validation;
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, v.User.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, v.User.Email));
        identity.AddClaim(new Claim(ClaimTypes.Email, v.User.Email));
        identity.AddClaim(new Claim(ClaimTypes.Role, v.User.RoleName));
        identity.AddClaim(new Claim(AmrClaimType, v.Amr));
        identity.AddClaim(new Claim("sid", v.SessionId.ToString()));

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private sealed record CachedSession(SessionValidation Validation, DateTime LastTouchedUtc);
}
