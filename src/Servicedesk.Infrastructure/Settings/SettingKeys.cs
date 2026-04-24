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
        public const string OrphanRetentionHours = "Storage.OrphanRetentionHours";
    }

    public static class Mail
    {
        public const string PollingIntervalSeconds = "Mail.PollingIntervalSeconds";
        public const string MaxBatchSize = "Mail.MaxBatchSize";
        public const string QuotedHistoryStripping = "Mail.QuotedHistoryStripping";
        public const string PlusAddressToken = "Mail.PlusAddressToken";
        public const string MarkAsReadOnIngest = "Mail.MarkAsReadOnIngest";
        public const string MoveOnIngest = "Mail.MoveOnIngest";
        public const string ProcessedFolderName = "Mail.ProcessedFolderName";
        public const string AutoLinkCompanyByDomain = "Mail.AutoLinkCompanyByDomain";
        public const string AutoLinkDomainBlacklist = "Mail.AutoLinkDomainBlacklist";
        public const string MaxOutboundTotalBytes = "Mail.MaxOutboundTotalBytes";
    }

    public static class Companies
    {
        public const string SearchLimit = "Companies.SearchLimit";
    }

    public static class Contacts
    {
        public const string PageSize = "Contacts.PageSize";
    }

    public static class Graph
    {
        public const string TenantId = "Graph.TenantId";
        public const string ClientId = "Graph.ClientId";
    }

    public static class Auth
    {
        // Microsoft / Azure AD login. Single-tenant — the tenant id is read
        // from Graph.TenantId (shared app-registration with the mail Graph
        // client). The client secret for OIDC is the same value stored in
        // ISecretProvider under the "GraphClientSecret" key; no separate
        // secret needed until an install requires distinct app-registrations
        // for mail and auth.
        public const string MicrosoftEnabled = "Auth.Microsoft.Enabled";
    }

    public static class Sla
    {
        public const string FirstContactTriggers = "Sla.FirstContact.Triggers";
        public const string PauseOnPending = "Sla.PauseOnPending";
        public const string HolidaysCountryCode = "Sla.Holidays.CountryCode";
        public const string HolidaysAutoSync = "Sla.Holidays.AutoSync";
        public const string DashboardShowAvgPickup = "Sla.Dashboard.ShowAvgPickupTile";
        public const string RecalcIntervalSeconds = "Sla.RecalcIntervalSeconds";
    }

    public static class Search
    {
        public const string MinQueryLength = "Search.MinQueryLength";
        public const string DropdownLimit = "Search.DropdownLimit";
        public const string DebounceMs = "Search.DebounceMs";
    }

    public static class App
    {
        /// Absolute base URL of this install (e.g. `https://desk.example.com`).
        /// Consumed by notification-mail templates to build CTA links that
        /// survive the round-trip to an agent's mailbox. Empty → links fall
        /// back to relative paths and a warning is logged so an admin can
        /// spot the misconfiguration.
        public const string PublicBaseUrl = "App.PublicBaseUrl";

        /// IANA time-zone id (e.g. `Europe/Brussels`). Drives the server time
        /// shown in the UI and the offsetMinutes returned by `/api/system/time`.
        /// Empty or invalid → server falls back to the container's local time
        /// (set via the `TZ` env-var, provisioned by install.sh). Business-
        /// hours schedules and SLA math stay on their per-schema timezone and
        /// are not affected by this value.
        public const string TimeZone = "App.TimeZone";
    }

    public static class Notifications
    {
        public const string MentionEmailEnabled = "Notifications.MentionEmailEnabled";
        public const string PopupDurationSeconds = "Notifications.PopupDurationSeconds";
    }

    public static class Jobs
    {
        public const string CompletedRetentionDays = "Jobs.CompletedRetentionDays";
        public const string DeadLetterAckedRetentionDays = "Jobs.DeadLetterAckedRetentionDays";
        public const string AttachmentMaxAttempts = "Jobs.AttachmentMaxAttempts";
        public const string AttachmentRetryBaseSeconds = "Jobs.AttachmentRetryBaseSeconds";
        public const string AttachmentWorkerConcurrency = "Jobs.AttachmentWorkerConcurrency";
        public const string AttachmentWorkerPollSeconds = "Jobs.AttachmentWorkerPollSeconds";
    }

    public static class IntakeForms
    {
        public const string DefaultExpiryDays = "IntakeForms.DefaultExpiryDays";
        public const string MaxQuestionsPerTemplate = "IntakeForms.MaxQuestionsPerTemplate";
        public const string MaxAnswerSizeBytes = "IntakeForms.MaxAnswerSizeBytes";
        public const string MaxTotalAnswersBytes = "IntakeForms.MaxTotalAnswersBytes";
        public const string ExpirySweepMinutes = "IntakeForms.ExpirySweepMinutes";
        public const string PublicRateLimitPermits = "IntakeForms.PublicRateLimit.PermitPerWindow";
        public const string PublicRateLimitWindowSeconds = "IntakeForms.PublicRateLimit.WindowSeconds";
    }

    public static class Health
    {
        // Security-activity subsystem (v0.0.18). Samples the audit_log over a
        // rolling window and raises an incident + admin push when one of the
        // categories exceeds its threshold. Categories collapse semantically
        // related event types (the five M365 reject reasons → one bucket).
        public const string SecurityActivityEnabled = "Health.SecurityActivity.Enabled";
        public const string SecurityActivityWindowSeconds = "Health.SecurityActivity.WindowSeconds";
        public const string SecurityActivityIntervalSeconds = "Health.SecurityActivity.IntervalSeconds";
        public const string SecurityActivityCriticalMultiplier = "Health.SecurityActivity.CriticalMultiplier";

        public const string SecurityActivityThresholdLoginFailed = "Health.SecurityActivity.Threshold.LoginFailed";
        public const string SecurityActivityThresholdLoginLockedOut = "Health.SecurityActivity.Threshold.LoginLockedOut";
        public const string SecurityActivityThresholdCsrfRejected = "Health.SecurityActivity.Threshold.CsrfRejected";
        public const string SecurityActivityThresholdRateLimited = "Health.SecurityActivity.Threshold.RateLimited";
        public const string SecurityActivityThresholdMicrosoftLoginRejected = "Health.SecurityActivity.Threshold.MicrosoftLoginRejected";
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
        new SettingDefault(SettingKeys.Storage.OrphanRetentionHours, "24", "int", "Storage",
            "How long (hours) a user-uploaded attachment that was never linked to a post or mail is kept before the orphan-sweeper deletes it."),

        // Mail — ADR-001 placeholders consumed from v0.0.8 step 4 onwards.
        new SettingDefault(SettingKeys.Mail.PollingIntervalSeconds, "60", "int", "Mail",
            "How often (seconds) the polling fallback checks each mailbox for new messages."),
        new SettingDefault(SettingKeys.Mail.MaxBatchSize, "50", "int", "Mail",
            "Maximum messages pulled per polling cycle per mailbox."),
        new SettingDefault(SettingKeys.Mail.QuotedHistoryStripping, "true", "bool", "Mail",
            "Strip quoted reply history before indexing body text for search. Full HTML is retained for display."),
        new SettingDefault(SettingKeys.Mail.PlusAddressToken, "TCK", "string", "Mail",
            "Plus-address token used in outbound Reply-To (e.g. servicedesk+TCK-1234@domain) and parsed from inbound recipients for threading."),
        new SettingDefault(SettingKeys.Mail.MarkAsReadOnIngest, "true", "bool", "Mail",
            "After a successful ticket-commit, mark the source message as read in the mailbox."),
        new SettingDefault(SettingKeys.Mail.MoveOnIngest, "true", "bool", "Mail",
            "After a successful ticket-commit, move the source message out of the inbox into the processed folder."),
        new SettingDefault(SettingKeys.Mail.ProcessedFolderName, "Servicedesk Verwerkt", "string", "Mail",
            "Mailbox folder name where ingested messages are moved. Auto-created at first use if missing."),
        new SettingDefault(SettingKeys.Mail.AutoLinkCompanyByDomain, "true", "bool", "Mail",
            "When true, contacts created during mail intake are automatically linked to a company matched on the sender's email domain (via the Companies → Domains list)."),
        new SettingDefault(SettingKeys.Mail.AutoLinkDomainBlacklist,
            "[\"gmail.com\",\"outlook.com\",\"hotmail.com\",\"live.com\",\"yahoo.com\",\"icloud.com\",\"me.com\",\"msn.com\",\"aol.com\",\"proton.me\",\"protonmail.com\",\"pm.me\",\"mail.com\",\"gmx.com\",\"gmx.net\",\"yandex.com\",\"yandex.ru\",\"zoho.com\",\"fastmail.com\",\"tutanota.com\",\"web.de\",\"t-online.de\",\"orange.fr\",\"laposte.net\",\"free.fr\",\"telenet.be\",\"skynet.be\"]",
            "json", "Mail",
            "JSON array of freemail/public domains that must never auto-link to a company. The Companies → Domains endpoint also refuses to store any of these as a company domain. Manual contact↔company linking is unaffected."),
        new SettingDefault(SettingKeys.Mail.MaxOutboundTotalBytes, "3145728", "int", "Mail",
            "Hard cap (bytes) on the combined size of attachments allowed on a single outbound mail. Default 3 MB matches Microsoft Graph's inline-fileAttachment limit; mails above this are rejected with a clear error."),

        // Companies — v0.0.9.
        new SettingDefault(SettingKeys.Companies.SearchLimit, "25", "int", "Companies",
            "Maximum number of results returned by the Companies global-search source."),

        // Contacts — v0.0.10.
        new SettingDefault(SettingKeys.Contacts.PageSize, "25", "int", "Contacts",
            "Default page size for the Contacts overview page. Requests may override via query string up to a hard cap."),

        // Graph — tenant/client id only. Client secret lives in ISecretProvider, never here.
        new SettingDefault(SettingKeys.Graph.TenantId, "", "string", "Graph",
            "Azure AD tenant ID. Shared across Microsoft Graph mail access and the M365 login flow (v0.0.13)."),
        new SettingDefault(SettingKeys.Graph.ClientId, "", "string", "Graph",
            "Application (client) ID registered in Azure AD for this install. Used by both the mail Graph client (app-only) and the M365 login flow (delegated OIDC) — one app-registration, two permission sets."),

        // Auth — v0.0.13 M365 login. Off by default so a fresh install
        // boots with local-only login until an admin fills in tenant/client
        // and adds the OIDC permissions + redirect URI in Azure Portal.
        new SettingDefault(SettingKeys.Auth.MicrosoftEnabled, "false", "bool", "Auth",
            "When true, the login page shows 'Sign in with Microsoft' and the /api/auth/microsoft/* endpoints are active. Requires Graph.TenantId + Graph.ClientId + GraphClientSecret to be set, and the app-registration must carry delegated openid/profile/email/User.Read permissions plus a redirect URI matching this install's public base URL."),

        // Search — v0.0.8 step 8. Tunables for the global search dropdown
        // and the full-page search. Exposed so installs can raise MinQueryLength
        // on very active instances to cut noise, or tighten the debounce to
        // make the dropdown feel snappier.
        new SettingDefault(SettingKeys.Search.MinQueryLength, "3", "int", "Search",
            "Minimum number of characters before the global search starts issuing queries."),
        new SettingDefault(SettingKeys.Search.DropdownLimit, "8", "int", "Search",
            "Maximum hits per source in the global-search dropdown."),
        new SettingDefault(SettingKeys.Search.DebounceMs, "150", "int", "Search",
            "Client-side debounce (milliseconds) between keystrokes and the dropdown query."),

        // Jobs — retention for the attachment job-queue and its history.
        new SettingDefault(SettingKeys.Jobs.CompletedRetentionDays, "7", "int", "Jobs",
            "Completed attachment jobs are hard-deleted after this many days."),
        new SettingDefault(SettingKeys.Jobs.DeadLetterAckedRetentionDays, "30", "int", "Jobs",
            "Dead-letter jobs acknowledged by an admin are retained this many days before deletion."),
        new SettingDefault(SettingKeys.Jobs.AttachmentMaxAttempts, "7", "int", "Jobs",
            "Max download tries before an attachment job is dead-lettered."),
        new SettingDefault(SettingKeys.Jobs.AttachmentRetryBaseSeconds, "5", "int", "Jobs",
            "Base for exponential backoff: delay = base * 2^(attempt-1) + jitter."),
        new SettingDefault(SettingKeys.Jobs.AttachmentWorkerConcurrency, "2", "int", "Jobs",
            "Number of parallel worker loops claiming attachment jobs."),
        new SettingDefault(SettingKeys.Jobs.AttachmentWorkerPollSeconds, "5", "int", "Jobs",
            "How often each worker loop polls for a new job when the queue is idle."),

        // SLA — v0.1.1. First-contact triggers are a JSON array of event types from
        // the ticket_events CHECK enum; any listed event marks the first-response
        // timer as met. Holidays auto-sync fetches public holidays for the
        // configured country from date.nager.at and refreshes yearly.
        new SettingDefault(SettingKeys.Sla.FirstContactTriggers,
            "[\"Mail\",\"Comment\"]",
            "json", "Sla",
            "Ticket event types that count as first contact and stop the first-response timer. Allowed: Mail, Comment, Note, StatusChange, AssignmentChange, QueueChange."),
        new SettingDefault(SettingKeys.Sla.PauseOnPending, "true", "bool", "Sla",
            "When the ticket enters status category 'Pending' (waiting on customer), pause the SLA timer."),
        new SettingDefault(SettingKeys.Sla.HolidaysCountryCode, "BE", "string", "Sla",
            "ISO-3166 alpha-2 country code used to auto-sync public holidays (BE, NL, DE, FR, ...). Empty disables auto-sync."),
        new SettingDefault(SettingKeys.Sla.HolidaysAutoSync, "true", "bool", "Sla",
            "When true, the holiday sync worker pulls this year + next from date.nager.at and refreshes daily."),
        new SettingDefault(SettingKeys.Sla.DashboardShowAvgPickup, "true", "bool", "Sla",
            "Show the 'Average first-response per queue' tile on the dashboard."),
        new SettingDefault(SettingKeys.Sla.RecalcIntervalSeconds, "60", "int", "Sla",
            "How often (seconds) the SLA recalc worker refreshes deadlines for open tickets."),

        // App — v0.0.12 stap 4. Absolute public URL is empty out-of-the-box;
        // the one-link installer (v0.0.15) will set it at provisioning time.
        // Until then, the notification-mail CTA falls back to relative paths
        // (which break when opened outside the browser session).
        new SettingDefault(SettingKeys.App.PublicBaseUrl, "", "string", "App",
            "Absolute public URL of this install (e.g. https://desk.example.com). Used to build deep-links in notification emails. Leave empty and a warning is logged; the installer fills this in automatically."),
        new SettingDefault(SettingKeys.App.TimeZone, "", "string", "App",
            "IANA time-zone id (e.g. Europe/Brussels, America/New_York). Drives the server clock shown in the UI and the offset returned by /api/system/time. Empty = fall back to the container's local time, which install.sh sets from the host TZ. Business-hours schedules and SLA math keep their own per-schema timezone."),

        // Notifications — v0.0.12 stap 4. Mention-trigger notification
        // raamwerk (@@-tag pipeline). Per-user preferences are out of scope
        // for this release — the global kill-switch covers the immediate
        // "too-noisy" case until we know what fine-grained control installs
        // actually want.
        new SettingDefault(SettingKeys.Notifications.MentionEmailEnabled, "true", "bool", "Notifications",
            "When true, a tagged agent receives an email from the ticket's queue mailbox on top of the in-app toast + navbar entry. Turn off on installs where the in-app channel is sufficient."),
        new SettingDefault(SettingKeys.Notifications.PopupDurationSeconds, "10", "int", "Notifications",
            "How long (seconds) the mention pop-up toast stays on screen before auto-dismissing. The navbar entry and history page are unaffected."),

        // Health — Security activity monitor (v0.0.18). Replaces "watch the
        // logs yourself". Defaults are tuned for a single-tenant install with
        // a few agents: noisy categories (login_failed, rate_limited) get a
        // higher bar than rare ones (csrf_rejected, locked_out, M365 reject).
        // Critical multiplier applies on top of every threshold — set to 1
        // to disable the Warning→Critical escalation entirely.
        new SettingDefault(SettingKeys.Health.SecurityActivityEnabled, "true", "bool", "Health",
            "When true, the security-activity subsystem samples the audit log on a rolling window and raises Health incidents + admin notifications when thresholds are exceeded."),
        new SettingDefault(SettingKeys.Health.SecurityActivityWindowSeconds, "3600", "int", "Health",
            "Rolling time window (seconds) over which security events are counted. Default 3600 = last hour."),
        new SettingDefault(SettingKeys.Health.SecurityActivityIntervalSeconds, "60", "int", "Health",
            "How often (seconds) the monitor samples the audit log and re-evaluates thresholds. Default 60s. Lower = faster alerts, more DB load."),
        new SettingDefault(SettingKeys.Health.SecurityActivityCriticalMultiplier, "3", "int", "Health",
            "Multiplier applied to each category threshold to flip Warning → Critical. E.g. login_failed threshold 10 + multiplier 3 → Warning at 10, Critical at 30 within the window. Set to 1 to keep everything Warning."),
        new SettingDefault(SettingKeys.Health.SecurityActivityThresholdLoginFailed, "10", "int", "Health",
            "Number of failed local-login attempts within the window before raising a Warning. Counts the 'login_failed' audit event."),
        new SettingDefault(SettingKeys.Health.SecurityActivityThresholdLoginLockedOut, "3", "int", "Health",
            "Number of account-lockouts within the window before raising a Warning. Counts the 'login_locked_out' audit event — each lockout already implies multiple failed attempts, so this threshold is intentionally low."),
        new SettingDefault(SettingKeys.Health.SecurityActivityThresholdCsrfRejected, "5", "int", "Health",
            "Number of CSRF-rejected requests within the window before raising a Warning. Counts the 'csrf_rejected' audit event — non-zero on a healthy install usually means an outdated browser tab; sustained activity indicates a real attempt."),
        new SettingDefault(SettingKeys.Health.SecurityActivityThresholdRateLimited, "50", "int", "Health",
            "Number of rate-limit rejections within the window before raising a Warning. Counts the 'rate_limited' audit event — set deliberately high because a single misbehaving client can hit this fast."),
        new SettingDefault(SettingKeys.Health.SecurityActivityThresholdMicrosoftLoginRejected, "5", "int", "Health",
            "Number of M365-login rejections within the window before raising a Warning. Sums all five reject reasons (unknown OID, disabled account, customer role, inactive, callback failure)."),

        // Intake Forms — v0.0.19. Customer-facing tokenised questionnaires.
        // Defaults are tuned for a small-to-medium helpdesk: a 14-day validity
        // window covers realistic customer response times including one weekend
        // stacked on top of a bank holiday, without leaving links indefinitely
        // exploitable. The 20/60 rate-limit is deliberately conservative —
        // each customer realistically GETs the page once and POSTs once, with
        // maybe one reload, so 20/min per {ip,token} is 10× the legitimate
        // traffic and catches brute-force token enumeration cleanly.
        new SettingDefault(SettingKeys.IntakeForms.DefaultExpiryDays, "14", "int", "IntakeForms",
            "Validity window (days) for a newly sent intake-form link. After this the link shows a 'formulier verlopen' page. Tunable per install; existing instances keep their original expires_utc."),
        new SettingDefault(SettingKeys.IntakeForms.MaxQuestionsPerTemplate, "50", "int", "IntakeForms",
            "Maximum number of questions (including section headers) allowed per template. Enforced server-side on template save; a bigger cap means a bigger submit payload for the customer."),
        new SettingDefault(SettingKeys.IntakeForms.MaxAnswerSizeBytes, "10240", "int", "IntakeForms",
            "Hard cap (bytes) on a single answer value submitted by a customer. Protects the DB from abuse via the long-text field. Above this the submit is rejected with 413."),
        new SettingDefault(SettingKeys.IntakeForms.MaxTotalAnswersBytes, "262144", "int", "IntakeForms",
            "Hard cap (bytes) on the total submitted payload (all answers combined). 413 on overflow."),
        new SettingDefault(SettingKeys.IntakeForms.ExpirySweepMinutes, "15", "int", "IntakeForms",
            "How often (minutes) the background worker flips Sent → Expired for instances past their expires_utc and writes an IntakeFormExpired ticket event."),
        new SettingDefault(SettingKeys.IntakeForms.PublicRateLimitPermits, "20", "int", "IntakeForms",
            "Requests permitted per rate-limit window against the public /api/intake-forms/{token} endpoints, partitioned by {ip,token}. Tune up only if legitimate customers hit the limit on reload."),
        new SettingDefault(SettingKeys.IntakeForms.PublicRateLimitWindowSeconds, "60", "int", "IntakeForms",
            "Rate-limit window length (seconds) for the public intake-form endpoints."),
    };
}
