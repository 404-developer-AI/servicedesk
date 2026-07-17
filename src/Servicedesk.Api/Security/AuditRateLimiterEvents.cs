using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Security;

/// Feeds rate-limit rejections into the audit log. Runs per rejected request
/// via the ASP.NET rate limiter's <c>OnRejected</c> hook.
public static class AuditRateLimiterEvents
{
    public const string EventTypeRateLimited = "rate_limited";

    /// <summary>
    /// Rejections on the CSP-report endpoint get their own audit event type
    /// (v0.0.92). Browsers fire violation reports autonomously — a burst of
    /// page refreshes can trip that endpoint's tight flood-protection limit
    /// without any hostile intent, so counting those rejections toward the
    /// generic "rate_limited" security-activity threshold produced false
    /// alarms. The separate type feeds a separate category with a much higher
    /// threshold, keeping a genuine report-flood visible without the noise.
    /// </summary>
    public const string EventTypeRateLimitedCspReport = "rate_limited_csp_report";

    public static async ValueTask OnRejected(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpCtx = context.HttpContext;
        var audit = httpCtx.RequestServices.GetService<IAuditLogger>();
        if (audit is not null)
        {
            try
            {
                await audit.LogAsync(new AuditEvent(
                    EventType: ResolveEventType(httpCtx.Request.Path),
                    Actor: httpCtx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    ActorRole: "anon",
                    Target: httpCtx.Request.Path.Value,
                    ClientIp: httpCtx.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: httpCtx.Request.Headers.UserAgent.ToString(),
                    Payload: new { method = httpCtx.Request.Method }), cancellationToken);
            }
            catch
            {
                // Audit failure must not mask the rate-limit response itself.
            }
        }

        httpCtx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpCtx.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal static string ResolveEventType(PathString path)
        => path.StartsWithSegments("/api/security/csp-report", StringComparison.OrdinalIgnoreCase)
            ? EventTypeRateLimitedCspReport
            : EventTypeRateLimited;
}
