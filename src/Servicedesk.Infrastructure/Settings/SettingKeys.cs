namespace Servicedesk.Infrastructure.Settings;

/// Canonical setting keys. String constants, not an enum, so the DB column
/// stays human-readable and ops can spot-check rows without a decoder.
public static class SettingKeys
{
    public static class Security
    {
        public const string RateLimitGlobalPermitPerWindow = "Security.RateLimit.Global.PermitPerWindow";
        public const string RateLimitGlobalWindowSeconds = "Security.RateLimit.Global.WindowSeconds";
        public const string RateLimitAuthPermitPerWindow = "Security.RateLimit.Auth.PermitPerWindow";
        public const string RateLimitAuthWindowSeconds = "Security.RateLimit.Auth.WindowSeconds";
        public const string HstsMaxAgeDays = "Security.Hsts.MaxAgeDays";
        public const string CspReportUri = "Security.Csp.ReportUri";

        public const string PasswordArgon2MemoryKb = "Security.Password.Argon2.MemoryKb";
        public const string PasswordArgon2Iterations = "Security.Password.Argon2.Iterations";
        public const string PasswordArgon2Parallelism = "Security.Password.Argon2.Parallelism";
        public const string PasswordMinimumLength = "Security.Password.MinimumLength";

        public const string LockoutMaxAttempts = "Security.Lockout.MaxAttempts";
        public const string LockoutWindowSeconds = "Security.Lockout.WindowSeconds";
        public const string LockoutDurationSeconds = "Security.Lockout.DurationSeconds";

        public const string SessionLifetimeHours = "Security.Session.LifetimeHours";
        public const string SessionIdleTimeoutMinutes = "Security.Session.IdleTimeoutMinutes";
        public const string SessionCookieName = "Security.Session.CookieName";

        public const string TwoFactorRequired = "Security.TwoFactor.Required";
        public const string TwoFactorTotpStepSeconds = "Security.TwoFactor.TotpStepSeconds";
        public const string TwoFactorTotpWindow = "Security.TwoFactor.TotpWindow";
        public const string TwoFactorRecoveryCodeCount = "Security.TwoFactor.RecoveryCodeCount";
    }
}

public sealed record SettingDefault(
    string Key,
    string Value,
    string ValueType,
    string Category,
    string Description);

public static class SettingDefaults
{
    public static readonly IReadOnlyList<SettingDefault> All = new[]
    {
        new SettingDefault(SettingKeys.Security.RateLimitGlobalPermitPerWindow, "120", "int", "Security",
            "Maximum requests per IP within the global rate limit window."),
        new SettingDefault(SettingKeys.Security.RateLimitGlobalWindowSeconds, "60", "int", "Security",
            "Global rate limit window length, in seconds."),
        new SettingDefault(SettingKeys.Security.RateLimitAuthPermitPerWindow, "10", "int", "Security",
            "Maximum /api/auth/* requests per IP within the auth rate limit window."),
        new SettingDefault(SettingKeys.Security.RateLimitAuthWindowSeconds, "60", "int", "Security",
            "Auth rate limit window length, in seconds."),
        new SettingDefault(SettingKeys.Security.HstsMaxAgeDays, "365", "int", "Security",
            "HSTS max-age sent in the Strict-Transport-Security header, in days."),
        new SettingDefault(SettingKeys.Security.CspReportUri, "/api/security/csp-report", "string", "Security",
            "Path the browser should POST CSP violation reports to."),

        new SettingDefault(SettingKeys.Security.PasswordArgon2MemoryKb, "65536", "int", "Security",
            "Argon2id memory cost in kibibytes. 65536 = 64 MiB."),
        new SettingDefault(SettingKeys.Security.PasswordArgon2Iterations, "3", "int", "Security",
            "Argon2id iteration count (time cost)."),
        new SettingDefault(SettingKeys.Security.PasswordArgon2Parallelism, "1", "int", "Security",
            "Argon2id degree of parallelism (lanes)."),
        new SettingDefault(SettingKeys.Security.PasswordMinimumLength, "12", "int", "Security",
            "Minimum length required for a local account password."),

        new SettingDefault(SettingKeys.Security.LockoutMaxAttempts, "5", "int", "Security",
            "Failed login attempts before the account is temporarily locked."),
        new SettingDefault(SettingKeys.Security.LockoutWindowSeconds, "900", "int", "Security",
            "Rolling window (seconds) within which failed attempts count toward lockout."),
        new SettingDefault(SettingKeys.Security.LockoutDurationSeconds, "900", "int", "Security",
            "How long (seconds) a locked-out account stays locked before it can try again."),

        new SettingDefault(SettingKeys.Security.SessionLifetimeHours, "12", "int", "Security",
            "Absolute session lifetime in hours. After this the user must log in again."),
        new SettingDefault(SettingKeys.Security.SessionIdleTimeoutMinutes, "60", "int", "Security",
            "Idle timeout in minutes. A session with no activity for this long is revoked."),
        new SettingDefault(SettingKeys.Security.SessionCookieName, "sd_session", "string", "Security",
            "Name of the httpOnly session cookie set on successful login."),

        new SettingDefault(SettingKeys.Security.TwoFactorRequired, "false", "bool", "Security",
            "When true, admins and agents must enroll TOTP before they can use the app."),
        new SettingDefault(SettingKeys.Security.TwoFactorTotpStepSeconds, "30", "int", "Security",
            "TOTP time step in seconds. RFC 6238 default is 30."),
        new SettingDefault(SettingKeys.Security.TwoFactorTotpWindow, "1", "int", "Security",
            "Accepted TOTP skew on either side of the current step (0 = strict, 1 = ±30s)."),
        new SettingDefault(SettingKeys.Security.TwoFactorRecoveryCodeCount, "10", "int", "Security",
            "Number of single-use recovery codes generated at TOTP enrollment."),
    };
}
