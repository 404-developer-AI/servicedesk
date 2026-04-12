using System.Security.Cryptography;
using System.Text;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Auth;

/// Double-submit CSRF protection. On unsafe verbs, requires the
/// <c>XSRF-TOKEN</c> cookie and <c>X-XSRF-TOKEN</c> header to match. Safe
/// verbs pass through untouched. Login, setup, and unauthenticated endpoints
/// are exempt: they're rate-limited by the <c>auth</c> policy and their
/// side-effects do not leak user state back to an attacker.
/// <para>
/// We do not use ASP.NET Core's built-in antiforgery because it is
/// MVC-centric and wants to emit tokens via razor helpers. A
/// ~40-line middleware is cleaner for a SPA over minimal APIs.
/// </para>
public sealed class DoubleSubmitCsrfMiddleware
{
    public const string CookieName = "XSRF-TOKEN";
    public const string HeaderName = "X-XSRF-TOKEN";

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE",
    };

    // Endpoints that must be reachable without a prior session to bootstrap.
    private static readonly string[] ExemptPrefixes =
    {
        "/api/auth/login",
        "/api/auth/setup",
        "/api/security/csp-report",
        "/hubs/",
    };

    private readonly RequestDelegate _next;

    public DoubleSubmitCsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAuditLogger audit)
    {
        if (SafeMethods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var exempt in ExemptPrefixes)
        {
            if (path.StartsWith(exempt, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }
        }

        var cookie = context.Request.Cookies[CookieName];
        var header = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) || !ConstantTimeEquals(cookie, header))
        {
            try
            {
                await audit.LogAsync(new AuditEvent(
                    EventType: AuthEventTypes.CsrfRejected,
                    Actor: context.User.Identity?.Name ?? "anon",
                    ActorRole: context.User.IsInRole("Admin") ? "Admin" : "anon",
                    Target: path,
                    ClientIp: context.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: context.Request.Headers.UserAgent.ToString()));
            }
            catch
            {
                // Never mask the 403 on an audit failure.
            }
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("CSRF token missing or mismatched.");
            return;
        }

        await _next(context);
    }

    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
