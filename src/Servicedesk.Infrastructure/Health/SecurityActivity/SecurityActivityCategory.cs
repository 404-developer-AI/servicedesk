using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Health.SecurityActivity;

/// One bucket the security-activity monitor tracks. Each bucket maps a
/// human-meaningful category (e.g. "M365 login rejected") onto one or more
/// raw audit-log <c>event_type</c> strings, plus the threshold-setting key
/// that controls when the category trips.
public sealed record SecurityActivityCategory(
    string Key,
    string Label,
    string ThresholdSettingKey,
    int DefaultThreshold,
    IReadOnlyList<string> EventTypes);

public static class SecurityActivityCategories
{
    /// Single source of truth: which raw audit events feed which category,
    /// and which Settings row controls the threshold. Add a category here +
    /// a matching threshold in <see cref="SettingKeys.Health"/> and the
    /// monitor / settings UI pick it up automatically — no other code needs
    /// to change.
    public static readonly IReadOnlyList<SecurityActivityCategory> All = new[]
    {
        new SecurityActivityCategory(
            Key: "login_failed",
            Label: "Failed logins",
            ThresholdSettingKey: SettingKeys.Health.SecurityActivityThresholdLoginFailed,
            DefaultThreshold: 10,
            EventTypes: new[] { AuthEventTypes.LoginFailed }),

        new SecurityActivityCategory(
            Key: "login_locked_out",
            Label: "Account lockouts",
            ThresholdSettingKey: SettingKeys.Health.SecurityActivityThresholdLoginLockedOut,
            DefaultThreshold: 3,
            EventTypes: new[] { AuthEventTypes.LoginLockedOut }),

        new SecurityActivityCategory(
            Key: "csrf_rejected",
            Label: "CSRF rejections",
            ThresholdSettingKey: SettingKeys.Health.SecurityActivityThresholdCsrfRejected,
            DefaultThreshold: 5,
            EventTypes: new[] { AuthEventTypes.CsrfRejected }),

        new SecurityActivityCategory(
            Key: "rate_limited",
            Label: "Rate-limit rejections",
            ThresholdSettingKey: SettingKeys.Health.SecurityActivityThresholdRateLimited,
            DefaultThreshold: 50,
            // String literal mirrors AuditRateLimiterEvents — the rate-limit
            // log path doesn't expose a public constant for it.
            EventTypes: new[] { "rate_limited" }),

        new SecurityActivityCategory(
            Key: "microsoft_login_rejected",
            Label: "M365 login rejections",
            ThresholdSettingKey: SettingKeys.Health.SecurityActivityThresholdMicrosoftLoginRejected,
            DefaultThreshold: 5,
            EventTypes: new[]
            {
                AuthEventTypes.MicrosoftLoginRejectedUnknown,
                AuthEventTypes.MicrosoftLoginRejectedDisabled,
                AuthEventTypes.MicrosoftLoginRejectedCustomer,
                AuthEventTypes.MicrosoftLoginRejectedInactive,
                AuthEventTypes.MicrosoftLoginFailedCallback,
            }),
    };

    /// Flat set of every audit event type this monitor cares about, used
    /// to bind the IN-list parameter on the audit_log count query.
    public static readonly IReadOnlyCollection<string> AllEventTypes =
        All.SelectMany(c => c.EventTypes).Distinct(StringComparer.Ordinal).ToArray();
}
