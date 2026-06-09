using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Timesheet;

/// Shared audit-write helper for the Timesheet endpoints. Same shape as
/// KbAudit so the audit-log query surface stays homogeneous.
internal static class TimesheetAudit
{
    public const string TaskCreated = "timesheet.task.created";
    public const string TaskUpdated = "timesheet.task.updated";

    public const string EntryCreated = "timesheet.entry.created";
    public const string EntryUpdated = "timesheet.entry.updated";
    public const string EntryDeleted = "timesheet.entry.deleted";

    // v0.0.74 — an agent set/cleared their own default Tab-1 task.
    public const string SelfDefaultTaskChanged = "timesheet.preferences.default_task_changed";

    // v0.0.35-C — manager edits to another user's row carry their own
    // event types so the audit-log surface can highlight "edited by
    // manager" vs "edited by owner" without re-deriving from the actor
    // column.
    public const string EntryEditedByManager = "timesheet.entry.edited_by_manager";
    public const string EntryDeletedByManager = "timesheet.entry.deleted_by_manager";

    // v0.0.54 — migration import surface. The enable/secret events carry a
    // session actor (admin config panel); the batch event is written by the
    // secret-gated surface, which has no session, so it stamps a fixed
    // synthetic actor instead.
    public const string ImportEnabledChanged = "timesheet.import.enabled_changed";
    public const string ImportSecretUpdated = "timesheet.import.secret_updated";
    public const string ImportSecretDeleted = "timesheet.import.secret_deleted";
    public const string ImportBatch = "timesheet.import.batch";

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
