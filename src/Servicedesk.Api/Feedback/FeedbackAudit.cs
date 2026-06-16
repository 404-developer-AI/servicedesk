using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Feedback;

/// Shared audit-write helper for the Employee Feedback endpoints. Same shape
/// as TimesheetAudit so the audit-log query surface stays homogeneous.
internal static class FeedbackAudit
{
    public const string EntryCreated = "feedback.entry.created";
    public const string EntryLogged = "feedback.entry.logged";
    public const string EntryUpdated = "feedback.entry.updated";
    public const string EntryDeleted = "feedback.entry.deleted";
    public const string EntryCompleted = "feedback.entry.completed";

    public const string TypeCreated = "feedback.type.created";
    public const string TypeUpdated = "feedback.type.updated";
    public const string TypeDeleted = "feedback.type.deleted";

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
