using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Portal;

/// Small per-request helpers shared by the portal endpoint files.
internal static class PortalRequest
{
    public static PortalCaller Caller(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(),
        Truncate(http.Request.Headers.UserAgent.ToString(), 512),
        http.Request.Host.HasValue ? http.Request.Host.Value : null);

    public static PortalActor Actor(HttpContext http)
    {
        var id = Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var g) ? g : Guid.Empty;
        return new PortalActor(
            id,
            http.User.Identity?.Name ?? id.ToString(),
            http.User.FindFirst(ClaimTypes.Role)?.Value ?? "anon",
            http.Connection.RemoteIpAddress?.ToString(),
            Truncate(http.Request.Headers.UserAgent.ToString(), 512));
    }

    public static Guid? UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var g) ? g : null;

    public static Guid? SessionId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst("sid")?.Value, out var g) ? g : null;

    /// v0.1.1 — true for an admin's read-only shadow session ("View portal
    /// as this customer"). RequireCustomer lets it through for reads; every
    /// portal write endpoint checks this and answers <see cref="ReadOnly"/>.
    public static bool IsImpersonated(HttpContext http) =>
        http.User.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value
            == SessionAuthenticationHandler.AmrImpersonated;

    /// The admin user id that minted a shadow session (impersonator claim).
    public static Guid? ImpersonatorId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(SessionAuthenticationHandler.ImpersonatorClaimType)?.Value, out var g) ? g : null;

    /// 403 for any write attempted through a shadow session. Server-side
    /// enforcement — the portal UI hides the same actions, but the amr is
    /// what actually guarantees the view stays read-only.
    public static IResult ReadOnly() => Results.Json(
        new { error = "impersonated_read_only", message = "This is a read-only view — changes are disabled while viewing the portal as a customer." },
        statusCode: StatusCodes.Status403Forbidden);

    public static bool IsCustomerPrincipal(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true
        && string.Equals(http.User.FindFirst(ClaimTypes.Role)?.Value, "Customer", StringComparison.Ordinal);

    public static Task<bool> PortalEnabledAsync(ISettingsService settings, CancellationToken ct) =>
        settings.GetAsync<bool>(SettingKeys.Portal.Enabled, ct);

    /// The portal answers 404 (not 403) while disabled so the surface is
    /// indistinguishable from "not installed".
    public static IResult Disabled() => Results.NotFound(new { error = "portal_disabled" });

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
}
