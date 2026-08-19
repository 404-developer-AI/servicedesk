using System.Security.Claims;
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
