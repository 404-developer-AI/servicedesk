using System.Security.Claims;

namespace Servicedesk.Api.Auth;

/// Helper for pulling the current actor's identity off <see cref="HttpContext"/>
/// in a consistent way so every endpoint audits the same shape: an actor
/// string (email when available, otherwise user id or "anon") and the role
/// that was on the session at the time of the call.
public static class ActorContext
{
    public static (string Actor, string Role) Resolve(HttpContext httpContext)
    {
        var user = httpContext.User;
        var email = user.FindFirst(ClaimTypes.Email)?.Value;
        var name = user.Identity?.Name;
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = user.FindFirst(ClaimTypes.Role)?.Value ?? "anon";
        return (email ?? name ?? id ?? "anon", role);
    }
}
