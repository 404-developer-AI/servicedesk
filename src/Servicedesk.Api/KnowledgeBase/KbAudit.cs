using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.KnowledgeBase;

/// Shared audit-write helper for the KB endpoints. Mirrors the inline
/// helper used by other endpoint files (CompanyEndpoints, TicketEndpoints)
/// but lives in a single place because the KB surface spans multiple files.
internal static class KbAudit
{
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
}
