using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Infrastructure.Activity;

/// Decorates <see cref="IAuditLogger"/> so a curated subset of audit
/// events is also persisted to the agent activity feed. Lives as a
/// decorator so existing call-sites do not change — every audit write
/// already happens via this interface, the wrapper just tees a row off.
///
/// The whitelist is intentional: many audit events (rate_limited,
/// csrf_rejected, …) are anonymous or hostile and don't belong in a
/// per-agent feed. Ticket events are also excluded because the Postgres
/// trigger on ticket_events already covers them and we don't want
/// duplicate rows.
public sealed class ActivityLoggingAuditDecorator : IAuditLogger
{
    private static readonly Dictionary<string, string> SummaryByEvent =
        new(StringComparer.Ordinal)
        {
            [AuthEventTypes.LoginSuccess]                 = "signed in",
            [AuthEventTypes.Logout]                       = "signed out",
            [AuthEventTypes.TwoFactorEnrolled]            = "enabled two-factor auth",
            [AuthEventTypes.TwoFactorDisabled]            = "disabled two-factor auth",
            [AuthEventTypes.PasswordChanged]              = "changed password",
            [AuthEventTypes.MicrosoftLoginSuccess]        = "signed in with Microsoft 365",
            [AuthEventTypes.UserCreatedLocal]             = "created a user (local)",
            [AuthEventTypes.UserCreatedMicrosoft]         = "created a user (Microsoft)",
            [AuthEventTypes.UserUpgradedMicrosoft]        = "upgraded a user to Microsoft",
            [AuthEventTypes.UserRoleChanged]              = "changed a user's role",
            [AuthEventTypes.UserActivated]                = "activated a user",
            [AuthEventTypes.UserDeactivated]              = "deactivated a user",
            [AuthEventTypes.UserDeleted]                  = "deleted a user",
            [AuthEventTypes.UserFeatureFlagsChanged]      = "changed a user's feature flags",
            [AuthEventTypes.UserDashboardTilesChanged]    = "changed a user's dashboard tiles",
            [AuthEventTypes.UserTimesheetFlagsChanged]    = "changed a user's timesheet flags",
            [AuthEventTypes.UserTimesheetPreferencesChanged] = "changed a user's timesheet preferences",
            [AuthEventTypes.UserIsoFlagsChanged]          = "changed a user's ISO flags",
            [AuthEventTypes.UserKbFlagChanged]            = "changed a user's KB flag",
            ["setting_changed"]                           = "changed a setting",
        };

    private readonly IAuditLogger _inner;
    private readonly IActivityRecorder _recorder;
    private readonly IUserService _users;
    private readonly ILogger<ActivityLoggingAuditDecorator> _logger;

    public ActivityLoggingAuditDecorator(
        IAuditLogger inner,
        IActivityRecorder recorder,
        IUserService users,
        ILogger<ActivityLoggingAuditDecorator> logger)
    {
        _inner = inner;
        _recorder = recorder;
        _users = users;
        _logger = logger;
    }

    public async Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
    {
        await _inner.LogAsync(evt, cancellationToken);

        if (evt is null) return;
        if (!SummaryByEvent.TryGetValue(evt.EventType, out var summary)) return;
        if (string.IsNullOrWhiteSpace(evt.Actor)) return;
        // Skip Customer-actor events — the feed tracks agents + admins.
        if (string.Equals(evt.ActorRole, "Customer", StringComparison.Ordinal)) return;

        try
        {
            var actor = await _users.FindByEmailAsync(evt.Actor, cancellationToken);
            if (actor is null) return;

            await _recorder.RecordAsync(new ActivityRecord(
                AgentId: actor.Id,
                EventType: evt.EventType,
                Summary: summary,
                EntityType: ResolveEntityType(evt.EventType),
                EntityId: evt.Target,
                Metadata: new
                {
                    target = evt.Target,
                    clientIp = evt.ClientIp,
                    userAgent = evt.UserAgent,
                }), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ActivityLoggingAuditDecorator failed to mirror event {EventType} for actor {Actor}.",
                evt.EventType, evt.Actor);
        }
    }

    private static string? ResolveEntityType(string eventType) => eventType switch
    {
        "setting_changed" => "setting",
        var t when t.StartsWith("user.", StringComparison.Ordinal) => "user",
        var t when t.StartsWith("auth.", StringComparison.Ordinal) => "session",
        AuthEventTypes.LoginSuccess => "session",
        AuthEventTypes.Logout => "session",
        AuthEventTypes.TwoFactorEnrolled => "profile",
        AuthEventTypes.TwoFactorDisabled => "profile",
        AuthEventTypes.PasswordChanged => "profile",
        _ => null,
    };
}
