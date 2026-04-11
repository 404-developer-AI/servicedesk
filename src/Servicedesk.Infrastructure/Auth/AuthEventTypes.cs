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
}
