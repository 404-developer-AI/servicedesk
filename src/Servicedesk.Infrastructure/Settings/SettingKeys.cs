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

    public static class Navigation
    {
        public const string ShowOpenTickets = "Navigation.ShowOpenTickets";
    }

    public static class Tickets
    {
        public const string DefaultPrioritySlug = "Tickets.DefaultPrioritySlug";
        public const string ListPageSize = "Tickets.ListPageSize";

        // Hook settings for v0.0.6+ portal/mail features. Rows exist now so
        // the knob is visible in Settings even though no code consumes them.
        public const string NewUserCreatesNotificationTicket = "Tickets.NewUserCreatesNotificationTicket";
        public const string SystemTicketsQueueSlug = "Tickets.SystemTicketsQueueSlug";

        public const string DefaultColumnLayout = "Tickets.DefaultColumnLayout";
    }

    public static class Storage
    {
        public const string BlobRoot = "Storage.BlobRoot";
        public const string MaxAttachmentBytes = "Storage.MaxAttachmentBytes";
        public const string RawEmlRetentionDays = "Storage.RawEmlRetentionDays";
        public const string InlineImageMaxBytes = "Storage.InlineImageMaxBytes";
        public const string BlobDiskWarnPercent = "Storage.BlobDiskWarnPercent";
        public const string BlobDiskCriticalPercent = "Storage.BlobDiskCriticalPercent";
        public const string PerMailboxMonthlyCapMB = "Storage.PerMailboxMonthlyCapMB";
    }

    public static class Mail
    {
        public const string PollingIntervalSeconds = "Mail.PollingIntervalSeconds";
        public const string MaxBatchSize = "Mail.MaxBatchSize";
        public const string QuotedHistoryStripping = "Mail.QuotedHistoryStripping";
    }

    public static class Graph
    {
        public const string TenantId = "Graph.TenantId";
        public const string ClientId = "Graph.ClientId";
    }

    public static class Jobs
    {
        public const string CompletedRetentionDays = "Jobs.CompletedRetentionDays";
        public const string DeadLetterAckedRetentionDays = "Jobs.DeadLetterAckedRetentionDays";
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

        new SettingDefault(SettingKeys.Navigation.ShowOpenTickets, "true", "bool", "Navigation",
            "Show the 'Open Tickets' link in the sidebar navigation."),

        new SettingDefault(SettingKeys.Tickets.DefaultPrioritySlug, "normal", "string", "Tickets",
            "Slug of the priority assigned to new tickets when none is specified."),
        new SettingDefault(SettingKeys.Tickets.ListPageSize, "50", "int", "Tickets",
            "Default number of rows returned per ticket list page (keyset paginated)."),
        new SettingDefault(SettingKeys.Tickets.NewUserCreatesNotificationTicket, "false", "bool", "Tickets",
            "When true, a system ticket is auto-created whenever a new user registers on the portal."),
        new SettingDefault(SettingKeys.Tickets.SystemTicketsQueueSlug, "", "string", "Tickets",
            "Slug of the queue that receives auto-generated system tickets."),
        new SettingDefault(SettingKeys.Tickets.DefaultColumnLayout,
            "number,subject,requester,companyName,queueName,statusName,priorityName,assigneeEmail,updatedUtc",
            "string", "Tickets",
            "Comma-separated column IDs shown by default in the ticket list for new users."),

        // Storage — ADR-001 (v0.0.8). Keys only; runtime consumers land in later steps.
        new SettingDefault(SettingKeys.Storage.BlobRoot, "/var/lib/servicedesk/blobs", "string", "Storage",
            "Host path for content-addressed blob storage. Bind-mounted into the container; read-only outside dev."),
        new SettingDefault(SettingKeys.Storage.MaxAttachmentBytes, "26214400", "int", "Storage",
            "Maximum size (bytes) for an individual attachment. Default 25 MB matches Exchange Online inbound."),
        new SettingDefault(SettingKeys.Storage.RawEmlRetentionDays, "0", "int", "Storage",
            "Retention window (days) for raw .eml copies. 0 = keep indefinitely."),
        new SettingDefault(SettingKeys.Storage.InlineImageMaxBytes, "2097152", "int", "Storage",
            "Maximum size (bytes) for inline images embedded in mail bodies. Default 2 MB."),
        new SettingDefault(SettingKeys.Storage.BlobDiskWarnPercent, "80", "int", "Storage",
            "Disk usage percentage that triggers a warning banner for admins."),
        new SettingDefault(SettingKeys.Storage.BlobDiskCriticalPercent, "92", "int", "Storage",
            "Disk usage percentage that pauses mail polling and raises a critical alert."),
        new SettingDefault(SettingKeys.Storage.PerMailboxMonthlyCapMB, "0", "int", "Storage",
            "Per-mailbox monthly ingestion cap in MB. 0 = no cap."),

        // Mail — ADR-001 placeholders consumed from v0.0.8 step 4 onwards.
        new SettingDefault(SettingKeys.Mail.PollingIntervalSeconds, "60", "int", "Mail",
            "How often (seconds) the polling fallback checks each mailbox for new messages."),
        new SettingDefault(SettingKeys.Mail.MaxBatchSize, "50", "int", "Mail",
            "Maximum messages pulled per polling cycle per mailbox."),
        new SettingDefault(SettingKeys.Mail.QuotedHistoryStripping, "true", "bool", "Mail",
            "Strip quoted reply history before indexing body text for search. Full HTML is retained for display."),

        // Graph — tenant/client id only. Client secret lives in ISecretProvider, never here.
        new SettingDefault(SettingKeys.Graph.TenantId, "", "string", "Graph",
            "Azure AD tenant ID used for Microsoft Graph mail access."),
        new SettingDefault(SettingKeys.Graph.ClientId, "", "string", "Graph",
            "Application (client) ID registered in Azure AD for this install."),

        // Jobs — retention for the attachment job-queue and its history.
        new SettingDefault(SettingKeys.Jobs.CompletedRetentionDays, "7", "int", "Jobs",
            "Completed attachment jobs are hard-deleted after this many days."),
        new SettingDefault(SettingKeys.Jobs.DeadLetterAckedRetentionDays, "30", "int", "Jobs",
            "Dead-letter jobs acknowledged by an admin are retained this many days before deletion."),
    };
}
