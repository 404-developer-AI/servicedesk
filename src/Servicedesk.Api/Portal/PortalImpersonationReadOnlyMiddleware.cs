using Microsoft.AspNetCore.Authentication;
using Servicedesk.Api.Auth;

namespace Servicedesk.Api.Portal;

/// v0.1.1 — the central write gate for shadow sessions ("View portal as this
/// customer"). Every unsafe verb on the customer-facing portal surface is
/// refused with 403 while the session's amr is "impersonated"; only the
/// portal logout (the banner's Exit button) passes. One middleware instead
/// of per-endpoint checks so a future portal endpoint can never forget the
/// guard — the endpoints keep their own checks as defence in depth.
public sealed class PortalImpersonationReadOnlyMiddleware
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE",
    };

    private readonly RequestDelegate _next;

    public PortalImpersonationReadOnlyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (SessionAuthenticationHandler.IsPortalSessionRequest(context.Request.Path)
            && !SafeMethods.Contains(context.Request.Method)
            && !context.Request.Path.StartsWithSegments("/api/portal/auth/logout"))
        {
            // The session scheme is not the pipeline default; authenticate
            // explicitly. Cheap: the handler caches validated sessions.
            var auth = await context.AuthenticateAsync(SessionAuthenticationHandler.SchemeName);
            var amr = auth.Principal?.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value;
            if (auth.Succeeded && amr == SessionAuthenticationHandler.AmrImpersonated)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "impersonated_read_only",
                    message = "This is a read-only view — changes are disabled while viewing the portal as a customer.",
                });
                return;
            }
        }

        await _next(context);
    }
}
