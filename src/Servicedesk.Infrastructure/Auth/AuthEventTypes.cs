namespace Servicedesk.Infrastructure.Auth;

/// Canonical audit event type strings for authentication events. Centralised
/// so the API, tests, and future admin tooling all agree on the exact names.
public static class AuthEventTypes
{
    public const string SetupWizardUsed = "setup_wizard_used";
    public const string LoginSuccess = "login_success";
    public const string LoginFailed = "login_failed";
    public const string LoginLockedOut = "login_locked_out";
    public const string Logout = "logout";
    public const string TwoFactorEnrolled = "2fa_enrolled";
    // v0.1.3 — enrollment start rotates the pending secret (and used to
    // silently disable a working setup pre-guard), so it leaves a trace too.
    public const string TwoFactorEnrollStarted = "2fa_enroll_started";
    public const string TwoFactorDisabled = "2fa_disabled";
    public const string TwoFactorChallengeSuccess = "2fa_challenge_success";
    public const string TwoFactorChallengeFailed = "2fa_challenge_failed";
    public const string SessionRevoked = "session_revoked";
    public const string CsrfRejected = "csrf_rejected";
    public const string PasswordChanged = "password_changed";

    // M365 login outcomes (v0.0.13). A successful M365 login writes
    // MicrosoftLoginSuccess; rejections carry a more specific suffix so an
    // admin reviewing the audit log can tell apart "OID not in our users
    // table" from "Graph reports the account as disabled" from "account
    // exists but is a Customer" at a glance.
    public const string MicrosoftLoginSuccess = "auth.microsoft.login.success";
    public const string MicrosoftLoginRejectedUnknown = "auth.microsoft.login.rejected_unknown";
    public const string MicrosoftLoginRejectedDisabled = "auth.microsoft.login.rejected_disabled";
    public const string MicrosoftLoginRejectedCustomer = "auth.microsoft.login.rejected_customer";
    public const string MicrosoftLoginRejectedInactive = "auth.microsoft.login.rejected_inactive";
    public const string MicrosoftLoginFailedCallback = "auth.microsoft.login.failed_callback";

    // Admin user-management (v0.0.13 step 3). All fire from
    // /api/admin/users/* under RequireAdmin; actor = current admin's
    // email, target = the affected user id.
    public const string UserCreatedLocal = "user.created.local";
    public const string UserCreatedMicrosoft = "user.created.m365";
    public const string UserUpgradedMicrosoft = "user.upgraded.m365";
    public const string UserRoleChanged = "user.role.changed";
    // v0.1.3 — admin-initiated password reset for a Local staff account.
    public const string UserPasswordReset = "user.password.reset";
    public const string UserActivated = "user.activated";
    public const string UserDeactivated = "user.deactivated";
    public const string UserDeleted = "user.deleted";

    // v0.0.35 timesheet feature flags. Single combined event with the
    // {enabled, manager} pair before/after in the payload, so a partial
    // toggle (turn on enabled while leaving manager unchanged) still
    // surfaces both fields in the audit row and an admin can reconstruct
    // the full state from a single entry.
    public const string UserTimesheetFlagsChanged = "user.timesheet_flags.changed";

    // v0.0.35-E — per-user Timesheet preference overrides (day start,
    // dag/week target, werkdagen). The payload carries the override fields
    // post-write, with null entries indicating "fell back to the global
    // default". Globals-changes are audited via the generic
    // 'setting_changed' event on /api/settings.
    public const string UserTimesheetPreferencesChanged = "user.timesheet_preferences.changed";

    // v0.0.40 — per-user ISO 27001 workflow flags. Single combined event
    // with the {mgm, dpo} pair in the payload, same shape as the
    // timesheet-flags event so an admin can reconstruct full state from
    // one audit row.
    public const string UserIsoFlagsChanged = "user.iso_flags.changed";

    // v0.0.40 polish — Knowledge Base per-user opt-in. Same shape as the
    // timesheet/iso event: payload carries the post-write boolean so an
    // admin can reconstruct the flip from one audit row.
    public const string UserKbFlagChanged = "user.kb_flag.changed";

    // Consolidated per-user feature-flag mutation (search + every existing
    // flag through the same endpoint). Payload carries the post-write
    // state of every flag so an admin reconstructing history from a
    // single row sees the full picture even when a partial update touched
    // only one field.
    public const string UserFeatureFlagsChanged = "user.feature_flags.changed";

    // Per-user Dashboard tile preferences. Payload carries the full set
    // of enabled tile-ids post-write so a single audit row reconstructs
    // what tiles the user can see after the change.
    public const string UserDashboardTilesChanged = "user.dashboard_tiles.changed";
}
