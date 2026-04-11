using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
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
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Servicedesk.Session";
    public const string AmrClaimType = "amr";

    private readonly ISessionService _sessions;
    private readonly ISettingsService _settings;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionService sessions,
        ISettingsService settings)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _settings = settings;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieName = await _settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var cookieValue = Request.Cookies[cookieName];
        if (string.IsNullOrWhiteSpace(cookieValue) || !Guid.TryParse(cookieValue, out var sessionId))
        {
            return AuthenticateResult.NoResult();
        }

        var idleMinutes = await _settings.GetAsync<int>(SettingKeys.Security.SessionIdleTimeoutMinutes);
        var validation = await _sessions.ValidateAsync(sessionId, TimeSpan.FromMinutes(idleMinutes), Context.RequestAborted);
        if (validation is null)
        {
            return AuthenticateResult.NoResult();
        }

        // Touch updates last_seen_utc. We don't await-chain it into the handler
        // response so even a transient DB blip cannot poison the request.
        _ = _sessions.TouchAsync(sessionId, Context.RequestAborted);

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, validation.User.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, validation.User.Email));
        identity.AddClaim(new Claim(ClaimTypes.Email, validation.User.Email));
        identity.AddClaim(new Claim(ClaimTypes.Role, validation.User.RoleName));
        identity.AddClaim(new Claim(AmrClaimType, validation.Amr));
        identity.AddClaim(new Claim("sid", validation.SessionId.ToString()));

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
