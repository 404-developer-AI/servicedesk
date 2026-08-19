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
    // /api/intake-forms/ is also exempt because the customer hitting that
    // link has no session, no cookie, and cannot be issued a CSRF token
    // before receiving the link in the mail. Rate limiting + single-shot
    // token semantics are the defence here (see Program.cs "intake-public"
    // policy). Only the two /{token}... routes live under that prefix;
    // admin + agent management endpoints live under /api/settings/... and
    // /api/tickets/{id}/intake-forms/... respectively and stay CSRF-enforced.
    private static readonly string[] ExemptPrefixes =
    {
        "/api/auth/login",
        "/api/auth/setup",
        "/api/security/csp-report",
        "/api/intake-forms/",
        // v0.0.38 — public survey endpoints follow the same model: the
        // customer hitting the link has no session and the token-hash
        // gate + rate limiter together provide the defence.
        "/api/public/surveys/",
        // v0.0.54 — the secret-gated timesheet migration surface is called
        // by the standalone migrator with no session and no cookie; the
        // X-Timesheet-Import-Token header (constant-time compared) plus the
        // admin enable-toggle are the defence. A browser CSRF attack cannot
        // forge that header. The admin config endpoints under
        // /api/admin/timesheet/import/ are NOT covered by this prefix and
        // stay session + CSRF enforced.
        "/api/timesheet/import/",
        // v0.1.0 — anonymous customer-portal auth endpoints (register,
        // verify-email, login, forgot/reset password, invitation accept).
        // No session exists yet; per-IP rate limiting + Turnstile (on the
        // registration form) + single-use hashed tokens are the defence.
        // The session-bound portal endpoints (/api/portal/auth/2fa/*,
        // /api/portal/tickets/*, /api/portal/admin/*) stay CSRF-enforced —
        // the portal login mints the XSRF cookie like the agent login.
        "/api/portal/auth/public/",
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
