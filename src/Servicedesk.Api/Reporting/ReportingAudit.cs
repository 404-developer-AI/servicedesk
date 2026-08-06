using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Reporting;

/// Audit-write helper for the Reporting API. Config events carry the admin
/// session actor; the secret-gated public surface has no session, so its
/// read/denied events stamp a fixed synthetic actor (the client IP still
/// lands in the row) — same split as the timesheet migration import.
internal static class ReportingAudit
{
    public const string EnabledChanged = "reporting.enabled_changed";
    public const string KeyUpdated = "reporting.key_updated";
    public const string KeyDeleted = "reporting.key_deleted";
    public const string Read = "reporting.read";
    public const string Denied = "reporting.denied";

    public static async Task WriteAsync(
        IAuditLogger audit, HttpContext http, string eventType, string target, object payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType, Actor: actor, ActorRole: role, Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    public static Task WriteMachineAsync(
        IAuditLogger audit, HttpContext http, string eventType, string target, object payload, CancellationToken ct) =>
        audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: "reporting-api",
            ActorRole: "System",
            Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);
}
