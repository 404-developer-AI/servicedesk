using System.Security.Claims;

namespace Servicedesk.Api.Auth;

/// The one place that reads the signed-in user's identity off a request or a
/// hub connection. Endpoints, filters and hubs never parse
/// <see cref="ClaimTypes.NameIdentifier"/> themselves (guarded by
/// <c>ActorContextUsageTests</c>), so "who is calling" has one definition.
public static class ActorContext
{
    /// Actor string for audit rows (email when available, otherwise name,
    /// user id or "anon") and the role that was on the session at the time.
    public static (string Actor, string Role) Resolve(HttpContext httpContext)
    {
        var user = httpContext.User;
        var email = user.FindFirst(ClaimTypes.Email)?.Value;
        var name = user.Identity?.Name;
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = TryGetRole(user) ?? "anon";
        return (email ?? name ?? id ?? "anon", role);
    }

    /// Strict: the signed-in user's id. Throws when the request carries no
    /// valid user id — endpoints behind <c>RequireAgent</c>/<c>RequireAdmin</c>/
    /// <c>RequireCustomer</c> never reach that case, so the throw is a
    /// defensive contract (a 500), never a silent fallback to another identity.
    public static Guid GetUserId(HttpContext httpContext) => GetUserId(httpContext.User);

    /// <inheritdoc cref="GetUserId(HttpContext)"/>
    public static Guid GetUserId(ClaimsPrincipal? user) =>
        TryGetUserId(user)
        ?? throw new InvalidOperationException("The request has no authenticated user id.");

    /// Lenient: the signed-in user's id, or null when there is none or the
    /// claim is not a valid Guid. For code that also runs anonymously
    /// (filters, middleware, hubs before auth is enforced).
    public static Guid? TryGetUserId(HttpContext httpContext) => TryGetUserId(httpContext.User);

    /// <inheritdoc cref="TryGetUserId(HttpContext)"/>
    public static Guid? TryGetUserId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && id != Guid.Empty
            ? id
            : null;

    /// Strict: the signed-in user's role (Customer / Agent / Admin) — what
    /// queue access and role gates are decided on. Throws when the session
    /// carries no role; behind a role policy that never happens, so the throw
    /// is a defensive contract, never a silent default role.
    public static string GetRole(HttpContext httpContext) => GetRole(httpContext.User);

    /// <inheritdoc cref="GetRole(HttpContext)"/>
    public static string GetRole(ClaimsPrincipal? user) =>
        TryGetRole(user) ?? throw new InvalidOperationException("The request has no authenticated role.");

    /// Lenient: the role, or null when there is none — for audit rows and
    /// code that also runs anonymously. Never use a fallback role to decide
    /// access.
    public static string? TryGetRole(HttpContext httpContext) => TryGetRole(httpContext.User);

    /// <inheritdoc cref="TryGetRole(HttpContext)"/>
    public static string? TryGetRole(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimTypes.Role)?.Value is { Length: > 0 } role ? role : null;
}
