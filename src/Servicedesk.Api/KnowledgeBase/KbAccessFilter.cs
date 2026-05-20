using System.Security.Claims;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.KnowledgeBase;

/// v0.0.40 polish — gates every /api/kb endpoint behind the per-user
/// `kb_enabled` flag. Layered on top of the existing role policies so
/// the agent-only/admin-only split stays intact: a caller without the
/// flag gets 403 even if their role would normally let them through.
///
/// One DB hit per request (no cache). KB calls are infrequent enough
/// that the simplicity wins over an eventually-consistent cache that
/// would mask an "admin just removed access" until expiry.
public static class KbAccessFilter
{
    public static RouteGroupBuilder RequireKbAccess(this RouteGroupBuilder builder)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var idClaim = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(idClaim, out var userId))
                return Results.Unauthorized();

            var users = http.RequestServices.GetRequiredService<IUserService>();
            var enabled = await users.GetKbEnabledAsync(userId, http.RequestAborted);
            if (!enabled)
                return Results.Forbid();

            return await next(context);
        });
        return builder;
    }
}
