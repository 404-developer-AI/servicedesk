using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Security;

/// Feeds rate-limit rejections into the audit log. Runs per rejected request
/// via the ASP.NET rate limiter's <c>OnRejected</c> hook.
public static class AuditRateLimiterEvents
{
    public static async ValueTask OnRejected(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpCtx = context.HttpContext;
        var audit = httpCtx.RequestServices.GetService<IAuditLogger>();
        if (audit is not null)
        {
            try
            {
                await audit.LogAsync(new AuditEvent(
                    EventType: "rate_limited",
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
}
