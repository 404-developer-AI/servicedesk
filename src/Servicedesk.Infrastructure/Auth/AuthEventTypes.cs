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
    public const string UserActivated = "user.activated";
    public const string UserDeactivated = "user.deactivated";
    public const string UserDeleted = "user.deleted";
}
