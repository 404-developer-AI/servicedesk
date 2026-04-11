namespace Servicedesk.Infrastructure.Settings;

/// Canonical setting keys for v0.0.3. String constants, not an enum, so the DB
/// column stays human-readable and ops can spot-check rows without a decoder.
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
    };
}
