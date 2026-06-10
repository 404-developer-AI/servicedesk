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

    /// v0.0.44 — UI-wide preferences. The default theme applies to new users
    /// and to existing users who have not yet picked a theme via Profile.
    /// Per-user override lives in <c>user_preferences</c> under the
    /// <c>ui:theme</c> key. Allowed values: <c>light</c> | <c>dark</c>.
    public static class Ui
    {
        public const string DefaultTheme = "Ui.DefaultTheme";
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

        public const string ShowContactNotLinkedWarning = "Tickets.ShowContactNotLinkedWarning";

        /// v0.0.57 — admin-configurable human-facing ticket reference prefix
        /// (e.g. "Ticket#"). Drives the copy-to-clipboard value, the outbound
        /// mail subject tag, the <c>#{ticket.reference}</c> template variable,
        /// and the survey-invite default text; the parser that resolves it back
        /// to a ticket (global search, picker, inbound subject threading) reads
        /// the same value. Bare numbers and a leading "#" are always accepted
        /// too, so changing this never strands anyone mid-paste.
        public const string ReferencePrefix = "Tickets.ReferencePrefix";
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

    /// Adsolut (Wolters Kluwer TAA) OAuth integration. Single-install,
    /// single-administration: one admin authorizes our access to one Adsolut
    /// dossier, all agents in this servicedesk read the synced data. The
    /// authorize/token endpoints are derived from the chosen environment;
    /// the redirect URI is computed from <see cref="App.PublicBaseUrl"/> at
    /// runtime so it always matches the install's host.
    public static class Adsolut
    {
        /// `production` (login.wolterskluwer.eu) or `uat`
        /// (login-stg.wolterskluwer.eu). Anything else falls back to
        /// production with a warning so a typo is not silently routed at
        /// the staging IdP.
        public const string Environment = "Adsolut.Environment";

        /// Client ID provisioned by Wolters Kluwer for this install. The
        /// matching client secret lives in protected_secrets under
        /// <c>Adsolut.ClientSecret</c>; both must be filled before the
        /// authorize-redirect endpoint accepts a request.
        public const string ClientId = "Adsolut.ClientId";

        /// Space-separated OAuth2 scopes appended to the authorize request.
        /// Adsolut scopes are API-specific (see api-portal.adsolut.com →
        /// per-API pages). The default `openid offline_access` covers the
        /// auth-only flow (id_token + refresh_token) — extend with the
        /// API scopes you intend to call once that's known.
        public const string Scopes = "Adsolut.Scopes";

        /// How many days before the refresh-token's sliding-month window
        /// elapses the Health subsystem flips the Adsolut card to Warning.
        /// The 30-day window itself is set by Wolters Kluwer; this only
        /// controls how early we surface "reconnect soon" to the admin.
        public const string RefreshWarnDays = "Adsolut.RefreshWarnDays";

        // Companies pull (Adsolut → Servicedesk). Worker tick cadence and
        // per-direction toggles. Sync-gating semantics (v0.0.27): every
        // toggle that *gates* whether sync runs at all defaults OFF and
        // gets force-reset to OFF on every (re)connect — so a fresh install
        // is silent until the admin explicitly opts in, and a reconnect can
        // never spring a surprise sync against a new dossier. Behaviour-
        // modifier toggles (LinkCompanyDomainsFromEmail) stay default ON
        // and are NOT touched on reconnect.
        public const string SyncIntervalMinutes = "Adsolut.Sync.IntervalMinutes";
        public const string SyncPullCompaniesUpdate = "Adsolut.Sync.Pull.Companies.Update";
        public const string SyncPullCompaniesCreate = "Adsolut.Sync.Pull.Companies.Create";
        public const string SyncIncludeSuppliers = "Adsolut.Sync.IncludeSuppliers";

        // v0.0.28 — Contacts pull (Adsolut → SD). Same gating discipline as
        // the Companies pull: default OFF, force-reset to OFF on every
        // (re)connect so a fresh install / new dossier is silent until the
        // admin explicitly opts in.
        public const string SyncPullContactsUpdate = "Adsolut.Sync.Pull.Contacts.Update";
        public const string SyncPullContactsCreate = "Adsolut.Sync.Pull.Contacts.Create";

        /// Cadence (hours) of the slow contacts-reconcile loop. Walks every
        /// Adsolut-linked SD company, refetches the full contacts list and
        /// reconciles state against SD. Catches active-flips (Adsolut does
        /// not bump <c>customer.lastModified</c> on a contact's true↔false
        /// flip) and hard-deletes that the fast delta-loop missed. Floor 1
        /// hour to keep load on Wolters Kluwer predictable.
        public const string SyncContactsReconcileIntervalHours = "Adsolut.Sync.Contacts.ReconcileIntervalHours";

        /// When true, the sync worker derives a domain from each Adsolut
        /// customer's email and inserts it into <c>company_domains</c> so
        /// inbound mail from the same domain auto-links to the company. The
        /// existing <see cref="Mail.AutoLinkDomainBlacklist"/> still applies
        /// — freemail / public domains never land here. Behaviour-modifier
        /// toggle: default ON, not reset on reconnect.
        public const string SyncLinkCompanyDomains = "Adsolut.Sync.LinkCompanyDomainsFromEmail";

        // v0.0.27 — Companies push (Servicedesk → Adsolut, customers-only).
        // Update + Create are independent gating-toggles: an admin can have
        // updates pushing without create-from-SD ever firing. Both default
        // OFF and get force-reset to OFF on every (re)connect. The two
        // supplier toggles are seeded as a placeholder for the v0.0.28
        // bidirectional-suppliers branch — UI surfaces them disabled with
        // an "In development" badge, and the SyncWorker force-ignores them
        // even if a SQL-side override is attempted.
        public const string PushUpdateExistingCustomers = "Adsolut.Push.UpdateExistingCustomers";
        public const string PushCreateNewCustomers = "Adsolut.Push.CreateNewCustomers";
        public const string PushUpdateExistingSuppliers = "Adsolut.Push.UpdateExistingSuppliers";
        public const string PushCreateNewSuppliers = "Adsolut.Push.CreateNewSuppliers";

        // v0.0.29 — Contacts push (Servicedesk → Adsolut, customers-only).
        // Mirror of the v0.0.28 contacts pull-toggles. Same gating discipline
        // as the Companies push: default OFF, force-reset to OFF on every
        // (re)connect. Update + Create are independent. Hard rule (enforced
        // in AdsolutContactPusher.LoadCandidatesAsync): never push a contact
        // whose parent company has no adsolut_id — Adsolut has no concept of
        // a free-floating contact.
        public const string SyncPushContactsUpdate = "Adsolut.Sync.Push.Contacts.Update";
        public const string SyncPushContactsCreate = "Adsolut.Sync.Push.Contacts.Create";

        /// Base URL of the Adsolut API (Administrations + Accounting share
        /// the same host). Documented at api.adsolut.com for production;
        /// no UAT mirror is documented today, but a future change can swap
        /// this without a code change. Trailing slash is normalised away.
        public const string ApiBaseUrl = "Adsolut.ApiBaseUrl";

        // ERP SalesReceipts (verkoopbonnen) pull. Separate, opt-in slice on
        // top of the Accounting integration: needs the WK.BE.ERP.Read scope
        // + a reconnect. Default OFF so Accounting-only installs are silent.
        /// Master toggle for mirroring Adsolut ERP sales receipts into the
        /// Timesheet → Adsolut tab. Off by default; flipping it on starts the
        /// SalesReceipts sync worker ticking (provided the integration is
        /// connected, a dossier is active, and the ERP scope is granted).
        public const string ErpSalesReceiptsEnabled = "Adsolut.Erp.SalesReceipts.Enabled";

        /// How often (minutes) the SalesReceipts sync worker ticks. Floor 5.
        public const string ErpSalesReceiptsSyncIntervalMinutes = "Adsolut.Erp.SalesReceipts.SyncIntervalMinutes";

        /// Comma-separated list of Adsolut state codes (e.g. "GEFAKT,AFG") the
        /// mirror keeps. EMPTY = keep all statuses. The admin ticks the
        /// statuses on the integration page; the list is populated dynamically
        /// from the state codes actually seen during sync. The Adsolut ERP API
        /// has no per-status query parameter, so filtering happens on our side
        /// during the sync (receipts whose state is not selected are skipped).
        public const string ErpSalesReceiptsStatusFilter = "Adsolut.Erp.SalesReceipts.StatusFilter";

        // ERP Orders (bestellingen) pull (v0.0.59). Same opt-in ERP slice as
        // SalesReceipts (WK.BE.ERP.Read scope). Default OFF.
        /// Master toggle for mirroring Adsolut ERP orders into the Orders
        /// overview (navbar → Assets → Orders). Off by default; flipping it on
        /// starts the Orders sync worker ticking (provided the integration is
        /// connected, a dossier is active, and the ERP scope is granted).
        public const string ErpOrdersEnabled = "Adsolut.Erp.Orders.Enabled";

        /// How often (minutes) the Orders sync worker ticks. Floor 5.
        public const string ErpOrdersSyncIntervalMinutes = "Adsolut.Erp.Orders.SyncIntervalMinutes";

        /// Comma-separated list of Adsolut order state codes (e.g. "OPEN") the
        /// overview + global search show. EMPTY = show all statuses. The admin
        /// ticks the statuses on the integration page; the list is populated
        /// dynamically from the state codes actually seen in the mirror. This
        /// filter is DISPLAY-ONLY — the mirror always holds every status; the
        /// selection only narrows what the overview and search surface.
        public const string ErpOrdersStatusFilter = "Adsolut.Erp.Orders.StatusFilter";

        /// JSON map of supplier-order ("bestelling") status code → hex colour,
        /// e.g. {"ONTV":"#22c55e","OPEN":"#f59e0b","NO_STATUS":"#9ca3af"}. Drives
        /// the coloured status chips in the order-detail "Bestellingen" block.
        /// The special key NO_STATUS colours order lines/orders with no linked
        /// supplier order. Edited on the integration page; empty = neutral.
        public const string ErpOrdersSupplierStatusColors = "Adsolut.Erp.Orders.SupplierStatusColors";

        // ERP Articles (artikels) pull (v0.0.76). Same opt-in ERP slice as
        // Orders/SalesReceipts (WK.BE.ERP.Read scope). Feeds the "Contract
        // Articles" module behind the Contracts hub. Default OFF.
        /// Master toggle for mirroring the Adsolut ERP article catalogue into
        /// the Contract Articles list (Contracts → Contract Articles). Off by
        /// default; flipping it on starts the Articles sync worker ticking
        /// (provided the integration is connected, a dossier is active, and the
        /// ERP scope is granted).
        public const string ErpArticlesEnabled = "Adsolut.Erp.Articles.Enabled";

        /// How often (minutes) the Articles sync worker ticks. Floor 5.
        public const string ErpArticlesSyncIntervalMinutes = "Adsolut.Erp.Articles.SyncIntervalMinutes";
    }

    /// Telavox call-popup integration (v0.0.34). Single-install model: one
    /// PAPI partner-token covers the whole servicedesk, per-agent CAPI
    /// tokens are auto-provisioned only after an admin has manually linked
    /// a Telavox-extension to an SD user via the integration page. The
    /// connector reads call-state only — no click-to-call, no presence-push,
    /// no status-writes back to Telavox. CAPI-user provisioning is the
    /// single exception (setup-time write).
    public static class Telavox
    {
        /// Master kill-switch. When false the polling worker is dormant, the
        /// integration page still loads for setup but no API calls are made.
        /// Default off so a fresh install is silent until an admin explicitly
        /// opts in (same discipline as Adsolut sync toggles).
        public const string Enabled = "Telavox.Enabled";

        /// Telavox PAPI partner customer-id. Discovered by the
        /// /test-connection action (GET /customers via PAPI) and pinned by
        /// the admin from the dropdown — never typed by hand. The matching
        /// PAPI partner-token lives in protected_secrets under
        /// Telavox.PartnerToken.
        public const string PartnerCustomerId = "Telavox.PartnerCustomerId";

        /// Idle CAPI poll cadence (seconds) — how often each linked agent
        /// is checked while no call is on the line. Default 10. The worker
        /// switches to the faster <see cref="RingingPollIntervalSeconds"/>
        /// the moment any agent's last-seen state is ringing, so the
        /// pickup-edge that fires the popup is caught quickly. Clamped to
        /// [2, 60] on read.
        public const string PollIntervalSeconds = "Telavox.PollIntervalSeconds";

        /// Ringing CAPI poll cadence (seconds) — used as soon as any
        /// linked agent's last-seen state is ringing/alerting, until the
        /// call is picked up or dropped. Default 1 so the answered-edge
        /// transition (the popup trigger in the default
        /// <see cref="PopupTriggerMode"/>) lands within a second of the
        /// actual pickup. Clamped to [1, 10] on read; should be &lt;= the
        /// idle interval.
        public const string RingingPollIntervalSeconds = "Telavox.RingingPollIntervalSeconds";

        /// Default ISO-3166 alpha-2 country code for phone-number parsing
        /// when a contact's phone field has no leading + (e.g. "0498 12 34
        /// 56" → +32498123456 when country=BE). Picked once per install
        /// since Servicedesk targets one customer per install. Changing
        /// after launch does NOT re-normalise existing rows; admins flip
        /// the backfill toggle to re-run normalisation if needed.
        public const string DefaultCountryCode = "Telavox.DefaultCountryCode";

        /// Which call-state transition fires the popup. Default 'answered'
        /// means the popup appears only after the agent has picked up
        /// (RINGING → ANSWERED on the agent's own extension), so a missed
        /// call or a ring-and-hangup doesn't spam the UI. Future modes
        /// ('ringing', 'either') can be wired without a schema change.
        public const string PopupTriggerMode = "Telavox.PopupTriggerMode";

        // Working-hours window — keeps the worker from polling Telavox
        // 24/7 on an install where agents are only available office hours.
        // Stored as server-time HH:mm strings so a clock-change doesn't
        // reinterpret the window. The day-mask is seven booleans (Mon..Sun)
        // joined as a comma list, e.g. "true,true,true,true,true,false,false".
        public const string PollWindowStart = "Telavox.PollWindow.Start";
        public const string PollWindowEnd = "Telavox.PollWindow.End";
        public const string PollWindowDays = "Telavox.PollWindow.Days";
        /// When true the worker fully sleeps outside the window; when false
        /// it falls back to a slow (60s) tick so very-late calls still pop.
        public const string PollWindowSleepOutsideHours = "Telavox.PollWindow.SleepOutsideHours";

        /// Base URL of the Telavox PAPI (partner API). Paths the client
        /// appends live under <c>/partner2/api/papi/v1/...</c>. Default
        /// targets <c>partner.telavox.se</c> per the published PAPI swagger
        /// (host + basePath fields); exposed as a setting so a regional or
        /// future PAPI host can be swapped without a code change. Trailing
        /// slashes are normalised.
        public const string PapiBaseUrl = "Telavox.PapiBaseUrl";

        /// Base URL of the Telavox CAPI (per-agent / customer-side API).
        /// Paths the worker appends live under <c>/api/capi/v1/...</c>.
        /// Default targets <c>home.telavox.se</c> per the published CAPI
        /// swagger. Distinct host from PAPI — the two APIs are NOT served
        /// from the same domain, which the v0.0.34 commit B-C code wrongly
        /// assumed.
        public const string CapiBaseUrl = "Telavox.CapiBaseUrl";

        /// Coalesce window (seconds) for the per-tick poll-summary row the
        /// worker writes to <c>integration_audit</c>. Without coalescing the
        /// worker writes one summary row per <see cref="PollIntervalSeconds"/>
        /// — at the default 2s cadence that's ~30 rows/minute even on a
        /// silent install. Default 300 (5 minutes) keeps the audit-log
        /// browsable without losing real-time fidelity: any tick with a
        /// fired popup or a per-agent failure flushes immediately, so the
        /// admin still sees those events in real time.
        public const string AuditSummaryIntervalSeconds = "Telavox.AuditSummaryIntervalSeconds";
    }

    /// Zammad migration link (v0.0.41). One-way bridge from an existing
    /// Zammad install into Servicedesk: connect, browse, dry-run, import.
    /// Single-install model — one base URL + one HTTP token per Servicedesk
    /// install. Knowledge-base, contacts and companies stay out of scope
    /// (contacts/companies come from Adsolut/CRM; if a Zammad ticket
    /// references a missing contact the importer skips it + reports).
    public static class Zammad
    {
        /// Master kill-switch. When false every Zammad endpoint refuses with
        /// 409 and the integration page surfaces "Disabled" — the setup
        /// fields remain editable so an admin can configure the connection
        /// while keeping the integration off. Default off; a fresh install
        /// is silent until the admin opts in.
        public const string Enabled = "Zammad.Enabled";

        /// Base URL of the source Zammad instance (e.g.
        /// <c>https://desk.example.com</c>). The client appends
        /// <c>/api/v1/...</c> to it. Trailing slashes are normalised.
        /// Validated to be http(s) at write time; non-https is rejected
        /// for any non-localhost host so a typo can't route the token over
        /// plaintext.
        public const string BaseUrl = "Zammad.BaseUrl";

        /// Page size used by the picker proxy when calling Zammad's
        /// <c>/tickets/search</c> + list endpoints. Clamped to [1, 200] on
        /// read. Default 50 keeps a result page snappy without round-trip
        /// chatter on large filter-matches.
        public const string PerPageDefault = "Zammad.PerPageDefault";

        /// Defensive rate limit applied client-side (token-bucket style)
        /// before issuing any Zammad request. Zammad publishes no rate
        /// limits; capping at 2/s on a fresh install avoids hammering a
        /// production helpdesk during a bulk dry-run. Clamped to [0.5, 20].
        public const string MaxRequestsPerSecond = "Zammad.MaxRequestsPerSecond";

        /// Base delay (seconds) for exponential backoff between retries
        /// when Zammad returns 429 / 5xx. Actual delay is
        /// <c>base * 2^attempt</c> plus ±20% jitter. Clamped to [1, 60].
        public const string RetryBaseSeconds = "Zammad.RetryBaseSeconds";

        /// Maximum retry attempts before a Zammad call surfaces as a hard
        /// failure to the caller. Clamped to [0, 8]. Default 3 covers a
        /// short upstream blip without dragging an admin-facing test out
        /// minutes deep.
        public const string RetryMaxAttempts = "Zammad.RetryMaxAttempts";

        /// Retention (days) for dry-run snapshots in
        /// <c>zammad_import_runs</c>. A dry-run carries the per-ticket
        /// mapping verdict + the resolved id-list a follow-up import will
        /// freeze on; that payload is bulky on a 5k-ticket migration, so
        /// we sweep aggressively. Clamped to [1, 90]. Default 14.
        public const string DryRunRetentionDays = "Zammad.DryRunRetentionDays";

        /// Hard cap on tickets walked per "Select all matching" dry-run
        /// / import. Stops a runaway filter (e.g. a free-text that
        /// accidentally matches the whole upstream) from blocking the
        /// worker for hours. Clamped to [100, 200000]. Default 20000.
        public const string SelectAllMatchingHardCap = "Zammad.SelectAllMatchingHardCap";
    }

    /// Tactical RMM integration (v0.0.52). One TRMM install per servicedesk
    /// install: a single base URL + a single API key drive a background
    /// poller that mirrors clients/sites/agents into our DB so the Assets
    /// page can filter and sort offline. Client name format is
    /// <c>[CODE] Customer Name</c> — the bracketed code matches a Company
    /// row's <c>code</c> field and is used for auto-linking.
    public static class Trmm
    {
        /// Master kill-switch. When false the sync worker is dormant and
        /// every Assets endpoint returns 409. The integration page stays
        /// editable for setup. Default off so a fresh install is silent
        /// until the admin opts in.
        public const string Enabled = "Trmm.Enabled";

        /// Base URL of the Tactical RMM API (e.g.
        /// <c>https://api.trmm.example.com</c>). The HTTP client appends
        /// <c>/clients/</c>, <c>/sites/</c>, <c>/agents/</c> below it.
        /// Trailing slashes are normalised. Non-https hosts other than
        /// localhost are rejected at write time so a typo can't route
        /// the API key over plaintext.
        public const string BaseUrl = "Trmm.BaseUrl";

        /// Background sync cadence (minutes). The worker pulls clients +
        /// sites + agents once per tick and upserts via DO UPDATE …
        /// RETURNING id so concurrent ticks are race-safe. Clamped to
        /// [1, 1440] on read. Default 15.
        public const string SyncIntervalMinutes = "Trmm.SyncIntervalMinutes";

        /// HTTP timeout (seconds) per TRMM API call. Clamped to [5, 300].
        /// Default 30. Lower = fail-fast on a slow upstream; higher =
        /// tolerate occasional latency without aborting a full sync.
        public const string RequestTimeoutSeconds = "Trmm.RequestTimeoutSeconds";
    }

    /// End-of-life data feed (v0.0.52). Background worker pulls the
    /// Microsoft Windows + Windows Server registries from
    /// <c>endoflife.date</c> on a configurable cadence and caches them
    /// in <c>eol_releases</c>. The Assets page then flags agents whose
    /// OS is past or near end-of-support — row tint + chip — without a
    /// live network call on render.
    public static class Eol
    {
        /// Master kill-switch. When false the refresh worker is dormant
        /// and the Assets page falls back to <c>unknown</c> for every
        /// agent (no row tint, no chip). Default on — the data source is
        /// public and free.
        public const string Enabled = "Eol.Enabled";

        /// Refresh cadence in days. Microsoft updates the registries
        /// infrequently, so a weekly poll is plenty. Clamped to [1, 90].
        public const string RefreshIntervalDays = "Eol.RefreshIntervalDays";

        /// How many days before the EOL date an agent gets flagged with
        /// the amber "soon" tint instead of the green "active" tint.
        /// Default 180 (6 months). Clamped to [1, 3650].
        public const string WarnThresholdDays = "Eol.WarnThresholdDays";

        /// Base URL of the endoflife.date API. Exposed as a setting so an
        /// air-gapped install can later point at a private mirror without
        /// a code change. Trailing slashes are normalised; the worker
        /// appends <c>/api/&lt;product&gt;.json</c>.
        public const string BaseUrl = "Eol.BaseUrl";
    }

    /// Generic integration-framework knobs shared by every connector. The
    /// per-integration tunables (Adsolut.*, Graph.*) stay in their own
    /// section; this group only carries the cross-cutting ones.
    public static class Integrations
    {
        /// How often (seconds) the integrations healthcheck worker ticks.
        /// Each tick: read connection state for every configured
        /// integration, write a heartbeat row to <c>integration_audit</c>,
        /// and push the resolved status to admins via SignalR. Floor 60s
        /// so an over-eager admin can't accidentally hammer Wolters Kluwer
        /// with refresh probes.
        public const string HealthcheckIntervalSeconds = "Integrations.Healthcheck.IntervalSeconds";

        /// Hours between active refresh probes performed by the
        /// healthcheck worker. A passive tick reads the cached connection
        /// state; an active tick actually calls the upstream token
        /// endpoint to verify the refresh token still works. Lower = catch
        /// a revoked RT sooner; higher = less load on the IdP. Default 12
        /// keeps the WK call-budget tiny while still flagging revocations
        /// within half a day.
        public const string HealthcheckActiveProbeHours = "Integrations.Healthcheck.ActiveProbeHours";

        /// Retention window (hours) for the high-volume operational rows in
        /// <c>integration_audit</c> — today the Telavox per-tick poll
        /// summary and the Adsolut healthcheck heartbeat. Admin-action
        /// rows (token updated, agent provisioned, …) and rare PAPI calls
        /// (customers list, api-user create/delete, …) are NEVER swept —
        /// only event-types explicitly on the firehose-list in code. The
        /// sweep runs piggyback on the healthcheck worker. Default 24h.
        public const string AuditRetentionHours = "Integrations.AuditRetentionHours";
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

        /// Maintenance-window banner. When Enabled is true, the banner shows
        /// app-wide and on the login page until the server clock passes
        /// EndUtc; after that the public endpoint reports `active: false`
        /// without flipping the toggle (admin still owns it). Start is shown
        /// in the banner copy ("from … until …") but is not gating: an admin
        /// turning the toggle on with a future Start surfaces the warning
        /// immediately.
        public const string MaintenanceEnabled = "App.Maintenance.Enabled";
        public const string MaintenanceStartUtc = "App.Maintenance.StartUtc";
        public const string MaintenanceEndUtc = "App.Maintenance.EndUtc";
        public const string MaintenanceMessage = "App.Maintenance.Message";

        /// Login banner — admin-controlled notice shown above the login card
        /// on every anonymous auth page (local login, M365 callback, future
        /// customer-portal login). Distinct from the maintenance banner so an
        /// admin can post a transient notice ("planned upgrade Sat 9:00") or
        /// a hard error ("storage degraded — open tickets only") without
        /// flipping the maintenance toggle, which has its own semantics.
        /// Message supports a constrained Markdown subset (bold/italic/links)
        /// — rendered HTML is sanitized client-side.
        public const string LoginBannerEnabled = "App.LoginBanner.Enabled";

        /// One of "info" | "warning" | "error". Drives banner colour + icon.
        /// Unknown values fall back to "info" so a hand-edited DB row can't
        /// blank the banner.
        public const string LoginBannerType = "App.LoginBanner.Type";

        public const string LoginBannerMessage = "App.LoginBanner.Message";
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
        public const string AutoPinSubmittedForms = "IntakeForms.AutoPinSubmittedForms";
    }

    // v0.0.40 — ISO 27001 workflow. One setting that admin populates
    // post-install: the queue id where ISO tickets live. When unset the
    // classification buttons never appear so the feature is fully
    // opt-in. Stored as a Guid string; an empty value = disabled.
    public static class Iso27001
    {
        public const string QueueId = "Iso27001.QueueId";
    }

    // v0.0.75 — public (no-login) Knowledge Base article links. Off by
    // default: every install that wants customer-reachable KB links flips
    // the switch deliberately. Only Published articles are ever served.
    public static class KnowledgeBase
    {
        public const string PublicLinksEnabled = "KnowledgeBase.PublicLinks.Enabled";
        public const string PublicRateLimitPermits = "KnowledgeBase.PublicRateLimit.PermitPerWindow";
        public const string PublicRateLimitWindowSeconds = "KnowledgeBase.PublicRateLimit.WindowSeconds";
    }

    public static class Surveys
    {
        public const string DefaultTtlDays = "Surveys.DefaultTtlDays";
        public const string ExpirySweepMinutes = "Surveys.ExpirySweepMinutes";
        public const string EnableAgentNotifications = "Surveys.EnableAgentNotifications";
        public const string InviteFromName = "Surveys.InviteFromName";
        public const string PublicRateLimitPermits = "Surveys.PublicRateLimit.PermitPerWindow";
        public const string PublicRateLimitWindowSeconds = "Surveys.PublicRateLimit.WindowSeconds";
        public const string MaxQuestionsPerSurvey = "Surveys.MaxQuestionsPerSurvey";
        public const string MaxCommentLength = "Surveys.MaxCommentLength";
    }

    public static class Triggers
    {
        // v0.0.24 — admin-configurable automation. Hard caps that keep a
        // misconfigured trigger from spiralling: chain depth on a single
        // ticket-mutation, dedup window for outbound mail-actions.
        public const string MaxChainPerMutation = "Triggers.MaxChainPerMutation";
        public const string MailDedupWindowMinutes = "Triggers.MailDedupWindowMinutes";

        // v0.0.24 Blok 5 — time-based activator scheduler. The worker ticks
        // every SchedulerIntervalSeconds and feeds the evaluator on three
        // boundaries: pending_till_utc reached (reminder), SLA deadline
        // reached (escalation), and SLA deadline minus EscalationWarningMinutes
        // (escalation_warning).
        public const string SchedulerIntervalSeconds = "Triggers.SchedulerIntervalSeconds";
        public const string EscalationWarningMinutes = "Triggers.EscalationWarningMinutes";
    }

    /// v0.0.35-E — Timesheet globals. Default start-tijd / dag-target /
    /// week-target / werkdagen apply to every Timesheet user that has not
    /// set a per-user override on their <c>users</c>-row. The per-user
    /// overrides live in <c>timesheet_*</c> columns on <c>users</c> and
    /// are NULL when the global default should win.
    public static class Timesheet
    {
        /// Local minute-of-day used as the "start" pre-fill on the first
        /// new entry of a day (Tab 1). The agent's first row defaults to
        /// this value; subsequent rows pre-fill from the previous row's
        /// end-time. Stored as minutes-since-midnight to match the entry
        /// schema (which also stores time as minutes). Default 510 = 08:30.
        public const string DefaultDayStartMinutes = "Timesheet.DefaultDayStartMinutes";

        /// Target work-minutes per work-day for the Tab-3 grid colour
        /// comparison. Default 480 (8h). Absence-minutes count toward the
        /// target, so a full Verlof-day is shown as "on target" rather
        /// than "8h short".
        public const string DefaultTargetMinutesPerDay = "Timesheet.DefaultTargetMinutesPerDay";

        /// Target work-minutes per ISO-week for the week-subtotal row in
        /// Tab 3. Default 2400 (40h).
        public const string DefaultTargetMinutesPerWeek = "Timesheet.DefaultTargetMinutesPerWeek";

        /// Set of weekdays counted as work-days, CSV of ISO weekday
        /// numbers (1=Mon..7=Sun). Drives the "Not filled" red badge for
        /// missing days in Tab 3. Default "1,2,3,4,5" (Mon–Fri).
        public const string DefaultWorkDays = "Timesheet.DefaultWorkDays";

        /// v0.0.36 — daily ceiling on absence-task minutes before the
        /// week is marked as "target not met". A task that has the
        /// `is_absence` flag set (e.g. Verlof, Ziek, Overig) counts
        /// against this ceiling; once any day in the ISO-week exceeds
        /// it the entire week flips to red regardless of total minutes.
        /// 0 = no ceiling (the previous behaviour). Default 30 min/day.
        public const string DefaultMaxAbsenceMinutesPerDay = "Timesheet.DefaultMaxAbsenceMinutesPerDay";

        /// v0.0.36 — office-hour window used by Tab 1 to flag row-to-row
        /// gaps and overlaps. A mismatch is highlighted in red iff the
        /// gap/overlap zone falls inside [start, end]. Stored as
        /// minutes-since-midnight, same as the day-start setting. The
        /// per-user override columns
        /// (timesheet_office_start_minutes / _end_minutes) take precedence.
        /// Default 510..1020 = 08:30..17:00.
        public const string DefaultOfficeStartMinutes = "Timesheet.DefaultOfficeStartMinutes";
        public const string DefaultOfficeEndMinutes   = "Timesheet.DefaultOfficeEndMinutes";

        /// v0.0.35-F — HTML fragments used by the "Import registered time"
        /// button on the ticket-reply editor. Header is emitted once before
        /// the rows, row is repeated per entry with placeholders, footer is
        /// emitted once after the rows with total placeholders.
        ///
        /// Row placeholders: {{date}}, {{start}}, {{end}}, {{duration}},
        /// {{minutes}}, {{description}}, {{agent}}, {{task}}.
        /// Footer placeholders: {{total_duration}}, {{total_minutes}},
        /// {{total_hours}}, {{count}}.
        ///
        /// Row data is HTML-escaped before substitution; the templates
        /// themselves are admin-controlled and emitted verbatim.
        public const string ReplyHeaderHtml = "Timesheet.ReplyHeaderHtml";
        public const string ReplyRowHtml = "Timesheet.ReplyRowHtml";
        public const string ReplyFooterHtml = "Timesheet.ReplyFooterHtml";

        /// v0.0.54 — master switch for the secret-gated migration import
        /// surface (Settings → Timesheet → Migration import). Defaults to
        /// FALSE (missing key reads as false), so a fresh install exposes
        /// nothing until an admin explicitly opts in AND configures the
        /// import token. The surface is only live when this is TRUE and the
        /// <c>Timesheet.ImportToken</c> protected-secret is set.
        public const string ImportEnabled = "Timesheet.ImportEnabled";

        /// Gross hourly rate (EUR) used to price registered hours. Drives the
        /// "Bruto Price" column on the Timesheet → Adsolut tab
        /// (rate × registered hours, per receipt and per task). Stored as a
        /// decimal string; read tolerantly (comma or dot). 0 / empty = the
        /// Bruto Price column stays blank.
        public const string HourlyRate = "Timesheet.HourlyRate";

        /// v0.0.56 — comma-separated list of status ids whose tickets feed
        /// the back-office "Resolved" tab. Selected by name in Settings →
        /// Timesheet → Back-office tabs, stored as ids so a status rename
        /// keeps the selection. Empty = the Resolved tab shows nothing.
        public const string ResolvedTabStatusIds = "Timesheet.ResolvedTabStatusIds";

        /// v0.0.56 — comma-separated list of status ids whose tickets feed
        /// the back-office "CWI" (Closed Without Invoice) tab. Same storage
        /// as ResolvedTabStatusIds. Empty = the CWI tab shows nothing.
        public const string CwiTabStatusIds = "Timesheet.CwiTabStatusIds";
    }

    /// v0.0.69 — Statistics feature. Status-group definitions used by the
    /// "Hours by status group" metric. Resolved/CWI reuse the back-office
    /// Timesheet sets above; QFI/WFQ are new, configured on the same
    /// Settings → Timesheet panel. Stored as a CSV of status ids.
    public static class Statistics
    {
        public const string QfiStatusIds = "Statistics.QfiStatusIds";
        public const string WfqStatusIds = "Statistics.WfqStatusIds";
    }

    /// v0.0.42 — Agent activity feed. Append-only event stream that
    /// captures every agent / admin action across the app. Visibility is
    /// per-user (users.activity_feed_enabled); retention is global and
    /// settings-driven.
    public static class ActivityFeed
    {
        public const string RetentionDays = "ActivityFeed.RetentionDays";
        public const string PruneIntervalHours = "ActivityFeed.PruneIntervalHours";
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

    /// v0.0.58 — Email signatures. Admin-managed, mailbox-scoped HTML
    /// signatures with per-sender variables filled from Microsoft Entra ID
    /// (with a per-user local override). The whole feature is opt-in: with
    /// <see cref="Enabled"/> off, no signature is ever appended on either the
    /// agent send path or the trigger send path.
    public static class Signatures
    {
        /// Master switch. Off by default so a fresh install never silently
        /// appends a signature until an admin has built one and opted in.
        public const string Enabled = "Signatures.Enabled";

        /// When true, the resolved signature is placed on replies too (directly
        /// under the agent's message, above the quoted history). When false,
        /// signatures are only added to the first/new outbound mail of a thread.
        public const string AppendOnReplies = "Signatures.AppendOnReplies";

        /// When true, the signature is pre-loaded into the compose window as a
        /// fixed, read-only block directly under the agent's message (above the
        /// quoted history), and that position is honoured on send. When false,
        /// the signature is appended at the very bottom of the mail at send time
        /// (the legacy behaviour). On by default.
        public const string ComposerPreload = "Signatures.ComposerPreload";

        /// Signature id (Guid string) used for trigger/automated mail, where
        /// there is no human sender to pull Entra variables from. Empty = no
        /// signature on system mail.
        public const string DefaultSystemSignatureId = "Signatures.DefaultSystemSignatureId";

        /// When true, the signature variable resolver pulls jobTitle /
        /// mobilePhone / businessPhones from Microsoft Entra ID for the sending
        /// agent. Requires the Graph app-registration to have User.Read.All
        /// (Application) consented. Off → only the per-user local profile
        /// fields are used. Degrades gracefully: a Graph failure falls back to
        /// the local fields rather than blocking the send.
        public const string EntraSyncEnabled = "Signatures.EntraSyncEnabled";

        /// When true (and EntraSyncEnabled is on), the resolver also fetches the
        /// agent's Entra profile photo for the {{Photo}} token. Separate toggle
        /// because the photo endpoint is a distinct, heavier Graph call.
        public const string EntraSyncPhotos = "Signatures.EntraSyncPhotos";

        /// Optional, admin-uploaded background/frame image for the profile-photo
        /// compositor (e.g. a brand "cloud" shape). Generic by design: uploaded
        /// per install, never shipped in code. Compositing happens client-side
        /// on a canvas; the flattened result is stored as each user's profile
        /// photo so the frame renders in every mail client. Empty = no frame.
        public const string PhotoFrameBlobHash = "Signatures.PhotoFrameBlobHash";
        public const string PhotoFrameMime = "Signatures.PhotoFrameMime";
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

        // Ui — v0.0.44 theming. Drives the initial theme for new users and for
        // existing users who have not yet chosen a theme on their Profile page.
        // Allowed values: 'light' | 'dark'. Unknown values fall back to 'light'
        // on read (factory default) so a hand-edited DB row can't break paint.
        new SettingDefault(SettingKeys.Ui.DefaultTheme, "light", "string", "Ui",
            "Default theme applied to new users and to users who have not yet picked a theme on their Profile page. Allowed values: 'light' or 'dark'. A user who has explicitly picked a theme keeps that choice across all devices via their saved preference; this setting only applies until they do."),

        new SettingDefault(SettingKeys.Tickets.DefaultPrioritySlug, "normal", "string", "Tickets",
            "Slug of the priority assigned to new tickets when none is specified."),
        new SettingDefault(SettingKeys.Tickets.ListPageSize, "1000", "int", "Tickets",
            "Maximum tickets loaded at once in the ticket list and saved views. The list loads in a single request (no lazy loading); a view with more matches than this shows the first N with a 'refine your filters' note. Clamped to a hard ceiling of 5000 to protect the browser and database."),
        new SettingDefault(SettingKeys.Tickets.NewUserCreatesNotificationTicket, "false", "bool", "Tickets",
            "When true, a system ticket is auto-created whenever a new user registers on the portal."),
        new SettingDefault(SettingKeys.Tickets.SystemTicketsQueueSlug, "", "string", "Tickets",
            "Slug of the queue that receives auto-generated system tickets."),
        new SettingDefault(SettingKeys.Tickets.DefaultColumnLayout,
            "number,subject,requester,companyName,queueName,statusName,priorityName,assigneeEmail,updatedUtc",
            "string", "Tickets",
            "Comma-separated column IDs shown by default in the ticket list for new users."),
        new SettingDefault(SettingKeys.Tickets.ShowContactNotLinkedWarning, "true", "bool", "Tickets",
            "Show a pulsing 'Contact not linked' warning in the ticket side panel when the requester has no current company links."),
        new SettingDefault(SettingKeys.Tickets.ReferencePrefix, "Ticket#", "string", "Tickets",
            "Human-facing prefix for a ticket reference (e.g. \"Ticket#\" produces \"Ticket#1234\"). Used by the copy-to-clipboard button, the outbound mail subject tag, the #{ticket.reference} template variable, and survey-invite default text. Pasting a reference in this form into global search, the ticket picker, or a timesheet link resolves it back to the ticket; a bare number or a leading \"#\" are always accepted as well."),

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

        // Adsolut — v0.0.25 OAuth integration. Empty client_id keeps the
        // tile in 'not configured' state until an admin pastes the WK-issued
        // credentials. Environment defaults to production so a misclick
        // doesn't silently route a real connect-attempt to UAT.
        new SettingDefault(SettingKeys.Adsolut.Environment, "production", "string", "Adsolut",
            "Wolters Kluwer login environment used for the OAuth dance. 'production' targets login.wolterskluwer.eu; 'uat' targets login-stg.wolterskluwer.eu (use this while Wolters Kluwer is still provisioning your real client). Switching environments does not invalidate stored tokens — it just redirects future authorize/refresh calls — but the refresh_token from one environment will not work against the other, so disconnect + reconnect after switching."),
        new SettingDefault(SettingKeys.Adsolut.ClientId, "", "string", "Adsolut",
            "Client ID provisioned by Wolters Kluwer for this servicedesk install. Paired with the client secret stored separately in the protected-secrets store. Both fields plus a registered redirect URI matching <PublicBaseUrl>/api/integrations/adsolut/callback must exist before the Connect button activates."),
        new SettingDefault(SettingKeys.Adsolut.Scopes,
            "openid offline_access profile WK.BE.Administrations WK.BE.Accounting.Read WK.BE.Accounting.Write",
            "string", "Adsolut",
            "Space-separated OAuth2 scopes appended to the authorize request. The v0.0.27 default covers both directions of the Companies sync (Read for the pull, Write for the push). Existing installs upgrading from v0.0.26 get WK.BE.Accounting.Write appended automatically by a one-shot data-migration; the saved scope set will then differ from the scope set bound to the active refresh token, which makes the 'Reconnect required' pill fire. Reconnect to mint a fresh RT with the write scope — without it every PUT/POST against /customers comes back as 403."),
        new SettingDefault(SettingKeys.Adsolut.RefreshWarnDays, "7", "int", "Adsolut",
            "Days before the refresh-token's sliding 1-month window expires the Health page flips the Adsolut card to Warning so the admin has time to test or reconnect. The 30-day window itself is enforced by Wolters Kluwer and not configurable from our side."),

        // Companies pull worker (v0.0.26 baseline, v0.0.27 default flip).
        // Sync-gating toggles default OFF — a fresh install must be silent
        // until the admin explicitly opts in. The reset-on-connect hook in
        // AdsolutAuthService.CompleteCallbackAsync force-resets these to
        // false on every successful (re)connect, so re-authorising against
        // a new dossier cannot accidentally start syncing inherited toggles.
        new SettingDefault(SettingKeys.Adsolut.SyncIntervalMinutes, "60", "int", "Adsolut",
            "How often (minutes) the Adsolut sync worker ticks. Each tick pulls Customers (and Suppliers if enabled) from the active administration using a delta-sync (?ModifiedSince=lastSuccessfulSync&OrderBy=lastModified). Floor 5 — set lower and the worker silently clamps. Default 60 is well below Adsolut's lastModified granularity and keeps Wolters Kluwer load minimal."),
        new SettingDefault(SettingKeys.Adsolut.SyncPullCompaniesUpdate, "false", "bool", "Adsolut",
            "When true, an Adsolut customer whose lastModified advanced overwrites the matched servicedesk Company on every sync tick. Match precedence: companies.adsolut_id (already linked) → companies.code (first link) → new row. Conflict tie-breaker: latest timestamp wins (companies.updated_utc vs Adsolut.lastModified) — local edits made after the Adsolut row's lastModified are preserved until Adsolut updates again. Default off + force-reset on every (re)connect so a fresh / re-linked install is silent until the admin opts in."),
        new SettingDefault(SettingKeys.Adsolut.SyncPullCompaniesCreate, "false", "bool", "Adsolut",
            "When true, an Adsolut customer with no matching servicedesk Company (no adsolut_id link, no code match) is inserted as a new Company on the next sync tick. Default off + force-reset on every (re)connect — turn on after spot-checking a few existing rows. Turn back off to keep the address book curated by hand and only refresh existing rows."),
        new SettingDefault(SettingKeys.Adsolut.SyncIncludeSuppliers, "false", "bool", "Adsolut",
            "When true, the sync worker also pulls Suppliers (crediteurs) from Adsolut alongside Customers. v0.0.27: 'In development' — backend force-ignores this even if turned on, UI shows the toggle disabled. Off by default because a helpdesk address book is debtors-first and most installs don't want utility/software vendor counterparties imported. The pull-update / pull-create toggles above will apply to suppliers too once this is unlocked in v0.0.28."),
        new SettingDefault(SettingKeys.Adsolut.SyncLinkCompanyDomains, "true", "bool", "Adsolut",
            "When true, the sync worker derives a domain from each Adsolut customer's email field (e.g. info@acme.com → acme.com) and inserts it into company_domains so inbound mail from acme.com auto-links to that company. The Mail.AutoLinkDomainBlacklist (gmail.com, outlook.com, …) is respected — freemail domains are never linked. Idempotent: a domain already claimed by another company is silently skipped (UNIQUE constraint). Adsolut itself has no website field, so this is the only way to populate the auto-link table from sync. Behaviour-modifier toggle: default ON and NOT reset on reconnect, so turning sync back on later does not silently disable domain auto-link."),

        // v0.0.28 — Contacts pull. Sync-gating: default OFF, force-reset on
        // (re)connect (same discipline as the Companies pull toggles).
        new SettingDefault(SettingKeys.Adsolut.SyncPullContactsUpdate, "false", "bool", "Adsolut",
            "When true, an Adsolut customer-contact whose lastModified advanced overwrites the matched SD contact + link on every sync tick. Match-key is the email (CITEXT, case-insensitive). Three Adsolut rows on three different customers with the same email are bundled into one SD contact + three contact_companies links; per-link state (UUID, active, lastModified, hash) is always synced, contact-level fields (first_name, last_name, phone, mobile_phone) follow LWW across all linked Adsolut rows. Default off + force-reset on every (re)connect."),
        new SettingDefault(SettingKeys.Adsolut.SyncPullContactsCreate, "false", "bool", "Adsolut",
            "When true, an Adsolut customer-contact with no matching SD contact (no email match, no UUID match) is inserted as a new contacts row + contact_companies link on the next tick. Adsolut contacts without an email are skipped + audit-logged regardless of this toggle (SD's email-keyed schema can't host them). Default off + force-reset on every (re)connect."),
        new SettingDefault(SettingKeys.Adsolut.SyncContactsReconcileIntervalHours, "24", "int", "Adsolut",
            "Hours between slow contacts-reconcile passes. Each pass walks every Adsolut-linked SD company and re-fetches the full contacts list to reconcile state — catches active-flips (Adsolut does not bump customer.lastModified on a contact's true↔false flip) and hard-deletes the fast delta-loop missed. Floor 1 hour so an over-eager admin can't accidentally hammer Wolters Kluwer with full sweeps. Default 24h."),
        new SettingDefault(SettingKeys.Adsolut.PushUpdateExistingCustomers, "false", "bool", "Adsolut",
            "When true, the sync worker pushes local edits on Adsolut-linked Companies back to Adsolut (PUT /customers/{id}) on the next tick. Push-tak only fires when the local row is strictly newer than the last pulled lastModified AND the canonical hash differs from the last-synced hash (so an echo-pull right after a push is a no-op). Independent of the create-toggle below: an admin can have updates pushing without ever pushing brand-new rows. Default off + force-reset on every (re)connect."),
        new SettingDefault(SettingKeys.Adsolut.PushCreateNewCustomers, "false", "bool", "Adsolut",
            "When true, the sync worker creates Adsolut customers from local Companies that have no adsolut_id yet (POST /customers). Independent of the update-toggle above: an admin can keep updates pushing while never auto-creating fresh rows in Adsolut. Default off + force-reset on every (re)connect — flip on deliberately after confirming the address book is the source of truth."),
        new SettingDefault(SettingKeys.Adsolut.PushUpdateExistingSuppliers, "false", "bool", "Adsolut",
            "v0.0.27 placeholder: pushing updates on linked Suppliers is in development. Backend force-ignores this; UI shows the toggle disabled with an 'In development' badge. Stays as a setting row so the v0.0.28 unlock is just a code-flip, not a schema migration."),
        new SettingDefault(SettingKeys.Adsolut.PushCreateNewSuppliers, "false", "bool", "Adsolut",
            "v0.0.27 placeholder: creating new Suppliers in Adsolut from SD is in development. Backend force-ignores this; UI shows the toggle disabled with an 'In development' badge. Stays as a setting row so the v0.0.28 unlock is just a code-flip, not a schema migration."),

        // v0.0.29 — Contacts push (SD → Adsolut, customers-only).
        new SettingDefault(SettingKeys.Adsolut.SyncPushContactsUpdate, "false", "bool", "Adsolut",
            "When true, the sync worker pushes local edits on the four mirrored contact fields (first_name, last_name, phone, mobile_phone) back to Adsolut (PUT /customers/{customer}/contacts/{contact}) on the next tick. Push-tak only fires when the contact's local updated_utc is strictly newer than the per-link adsolut_last_modified AND the canonical hash differs from the last-synced hash (so an echo-pull right after a push is a no-op). Hard rule: contacts whose parent company has no adsolut_id are never pushed — Adsolut has no concept of a free-floating contact. Default off + force-reset on every (re)connect."),
        new SettingDefault(SettingKeys.Adsolut.SyncPushContactsCreate, "false", "bool", "Adsolut",
            "When true, the sync worker creates Adsolut customer-contacts from SD contact_companies links that have no adsolut_contact_id yet (POST /customers/{customer}/contacts), provided the parent company is already Adsolut-linked. Independent of the update-toggle: an admin can have updates pushing while never auto-creating fresh rows in Adsolut. Default off + force-reset on every (re)connect."),

        new SettingDefault(SettingKeys.Adsolut.ApiBaseUrl, "https://api.adsolut.com", "string", "Adsolut",
            "Base URL of the Adsolut API. The Administrations service lives under /adm/v1, the Accounting service under /acc/v1, the ERP service under /erp/v1. Default targets api.adsolut.com (production); no UAT mirror is documented today but the value is exposed so a future change can swap it without a code release. Trailing slashes are normalised."),

        new SettingDefault(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, "false", "bool", "Adsolut",
            "When true, the SalesReceipts sync worker mirrors Adsolut ERP sales receipts (verkoopbonnen) into the Timesheet → Adsolut tab. Requires the WK.BE.ERP.Read scope on the active connection (tick it in the scopes picker + reconnect) and an active dossier. Off by default — Accounting-only installs stay silent. Each tick lists receipts (always IncludeFinishedState=true, since invoiced/finished receipts are excluded by default), fetches each by-id for the full line detail (the list view omits performance lines), and upserts the header + product lines + performance lines."),
        new SettingDefault(SettingKeys.Adsolut.ErpSalesReceiptsSyncIntervalMinutes, "60", "int", "Adsolut",
            "How often (minutes) the Adsolut SalesReceipts sync worker ticks. Floor 5 — set lower and the worker silently clamps. Independent from the Companies sync interval. Each tick is a delta-sync keyed on the receipt's lastModified (?ModifiedSince=lastSuccessfulSync)."),
        new SettingDefault(SettingKeys.Adsolut.ErpSalesReceiptsStatusFilter, "", "string", "Adsolut",
            "Comma-separated Adsolut state codes (e.g. 'GEFAKT,AFG') the SalesReceipts mirror keeps. Empty = keep all statuses. Ticked by the admin on the integration page; the available statuses are discovered dynamically from the receipts seen during sync. The Adsolut ERP API has no per-status query parameter, so receipts whose state is not selected are skipped on our side during the sync."),

        new SettingDefault(SettingKeys.Adsolut.ErpOrdersEnabled, "false", "bool", "Adsolut",
            "When true, the Orders sync worker mirrors Adsolut ERP orders (bestellingen) into the Orders overview (navbar → Assets → Orders). Requires the WK.BE.ERP.Read scope on the active connection (tick it in the scopes picker + reconnect) and an active dossier. Off by default — Accounting-only installs stay silent. Unlike SalesReceipts the OrderInfos list returns the full order incl. its detail lines inline, so each tick upserts straight from the list (no per-order by-id fetch). Header totals (excl/incl VAT) are stored verbatim."),
        new SettingDefault(SettingKeys.Adsolut.ErpOrdersSyncIntervalMinutes, "60", "int", "Adsolut",
            "How often (minutes) the Adsolut Orders sync worker ticks. Floor 5 — set lower and the worker silently clamps. Independent from the Companies + SalesReceipts sync intervals. Each tick is a delta-sync keyed on the order's lastModified (?ModifiedSince=lastSuccessfulSync)."),
        new SettingDefault(SettingKeys.Adsolut.ErpOrdersStatusFilter, "", "string", "Adsolut",
            "Comma-separated Adsolut order state codes (e.g. 'OPEN') the Orders overview + global search show. Empty = show all statuses. Ticked by the admin on the integration page; the available statuses are discovered dynamically from the orders seen in the mirror. DISPLAY-ONLY: the mirror always holds every status; this selection only narrows what the overview and global search surface — deselecting a status hides it (it is never purged) and re-ticking it shows it again with no re-sync."),
        new SettingDefault(SettingKeys.Adsolut.ErpOrdersSupplierStatusColors, "", "string", "Adsolut",
            "JSON map of Adsolut supplier-order ('bestelling') status code to hex colour, e.g. {\"ONTV\":\"#22c55e\",\"OPEN\":\"#f59e0b\",\"NO_STATUS\":\"#9ca3af\"}. Drives the coloured status chips in the order-detail Bestellingen block. The special key NO_STATUS colours lines/orders without a linked supplier order. Edited on the integration page; empty = neutral grey."),

        new SettingDefault(SettingKeys.Adsolut.ErpArticlesEnabled, "false", "bool", "Adsolut",
            "When true, the Articles sync worker mirrors the Adsolut ERP article catalogue (artikels) into the Contract Articles list (Contracts → Contract Articles). Requires the WK.BE.ERP.Read scope on the active connection (tick it in the scopes picker + reconnect) and an active dossier. Off by default — Accounting-only installs stay silent. The Articles list returns full article records inline (code, multi-language name/description, vat code) via cursor pagination, so each tick upserts straight from the list. Per-user access is the Contracts feature flag."),
        new SettingDefault(SettingKeys.Adsolut.ErpArticlesSyncIntervalMinutes, "60", "int", "Adsolut",
            "How often (minutes) the Adsolut Articles sync worker ticks. Floor 5 — set lower and the worker silently clamps. Independent from the Companies + SalesReceipts + Orders sync intervals. Each tick is a delta-sync keyed on the article's lastModified (?ModifiedSince=lastSuccessfulSync)."),

        // Telavox — v0.0.34 call-popup integration. Sync-gating discipline:
        // Enabled defaults OFF so a fresh install is silent until an admin
        // pastes the partner-token + picks the customer-id. Phone parsing
        // and the working-hours window pick reasonable BE-tuned defaults
        // so the integration page only really needs the two secrets +
        // per-agent mappings to be useful.
        new SettingDefault(SettingKeys.Telavox.Enabled, "false", "bool", "Telavox",
            "Master kill-switch. When true and the partner-token + customer-id are set, the polling worker ticks once per agent every PollIntervalSeconds (inside the working-hours window) and pushes IncomingCallAnswered SignalR events. Off by default so the integration page is visible for setup without firing any API call."),
        new SettingDefault(SettingKeys.Telavox.PartnerCustomerId, "", "string", "Telavox",
            "Telavox PAPI partner customer-id. Don't type this by hand — the integration page's Test connection action calls PAPI /customers with the saved partner-token and lets the admin pick the right id from the dropdown. Empty = the integration is unconfigured even if the partner-token is set."),
        new SettingDefault(SettingKeys.Telavox.PollIntervalSeconds, "10", "int", "Telavox",
            "Idle CAPI poll cadence in seconds — how often each linked agent is checked while no call is on the line. Default 10 keeps Telavox-side load minimal on quiet installs. The worker switches to RingingPollIntervalSeconds the moment any agent's last-seen state is ringing/alerting, so the answered-edge that fires the popup is still caught quickly. Clamped to [2, 60] on read. Outside the working-hours window the worker falls back to a slow tick (or sleeps fully, per PollWindow.SleepOutsideHours)."),
        new SettingDefault(SettingKeys.Telavox.RingingPollIntervalSeconds, "1", "int", "Telavox",
            "Ringing CAPI poll cadence in seconds — used as soon as any linked agent is currently ringing/alerting, until that call is picked up or dropped. Default 1 so the answered-edge transition (the popup trigger in the default PopupTriggerMode) lands within a second of the actual pickup. Clamped to [1, 10] on read; should be <= the idle interval. Raise to 2-3 if Telavox-side load complains during heavy call hours."),
        new SettingDefault(SettingKeys.Telavox.DefaultCountryCode, "BE", "string", "Telavox",
            "ISO-3166 alpha-2 country used to parse phone-numbers without an international prefix (e.g. '0498 12 34 56' → +32498123456 when set to BE). One value per install — the SD targets one customer per install so a single default is fine. Changing this does NOT retroactively re-normalise existing rows."),
        new SettingDefault(SettingKeys.Telavox.PopupTriggerMode, "answered", "string", "Telavox",
            "Which CAPI call-state transition fires the popup on the matched agent. 'answered' (default) waits for RINGING → ANSWERED on the agent's own extension, so a missed call or a quick ring-and-hangup never noise the UI. Future modes ('ringing', 'either') can be wired without a schema change."),
        new SettingDefault(SettingKeys.Telavox.PollWindowStart, "08:00", "string", "Telavox",
            "Server-time start of the daily polling window (HH:mm, 24h). Inside the window each linked agent ticks every PollIntervalSeconds; outside it the worker either sleeps or falls back to a 60s slow-tick. Set Start = End to disable the window (always-on polling)."),
        new SettingDefault(SettingKeys.Telavox.PollWindowEnd, "18:00", "string", "Telavox",
            "Server-time end of the daily polling window (HH:mm, 24h). See PollWindow.Start. Crossing midnight is supported (e.g. 22:00–06:00 night-shift) — the worker interprets End < Start as 'window wraps midnight'."),
        new SettingDefault(SettingKeys.Telavox.PollWindowDays, "true,true,true,true,true,false,false", "string", "Telavox",
            "Day-mask for the polling window: seven comma-separated booleans Mon..Sun. Default is workweek Mon-Fri. Outside the listed days the worker treats the install as out-of-hours regardless of the time-window."),
        new SettingDefault(SettingKeys.Telavox.PollWindowSleepOutsideHours, "true", "bool", "Telavox",
            "When true (default) the worker fully sleeps outside the working-hours window — no Telavox calls at all. When false it falls back to a 60s slow-tick so very-late calls still surface a popup, at the cost of round-the-clock Telavox-side load. Flip off only if your agents take calls outside the configured hours."),
        new SettingDefault(SettingKeys.Telavox.PapiBaseUrl, "https://partner.telavox.se", "string", "Telavox",
            "Base URL of the Telavox PAPI (partner API) host. Endpoints the SD client appends live under /partner2/api/papi/v1/... — matches the host + basePath fields of the published PAPI swagger. Trailing slashes are normalised. Swap only if your partner-token was issued for a non-default regional PAPI host."),
        new SettingDefault(SettingKeys.Telavox.CapiBaseUrl, "https://home.telavox.se", "string", "Telavox",
            "Base URL of the Telavox CAPI (customer / per-agent API) host. Endpoints the SD worker appends live under /api/capi/v1/... — matches the host + basePath of the published CAPI swagger. Distinct from the PAPI host; the two APIs are not served from the same domain."),
        new SettingDefault(SettingKeys.Telavox.AuditSummaryIntervalSeconds, "300", "int", "Telavox",
            "Coalesce window (seconds) for the per-tick poll-summary row the worker writes to integration_audit. Without coalescing the worker writes one row per PollIntervalSeconds — at the default 2s cadence that's ~30 rows/min even on a silent install. Default 300 (5 minutes) keeps the audit-log browsable; any tick with a fired popup or a per-agent failure flushes immediately so admins still see those events in real time. Floor 30s."),

        // Zammad — v0.0.41 migration link. Single-install: one base URL + one
        // HTTP token. Enabled defaults OFF so a fresh install is silent until
        // an admin pastes the token + URL and verifies via Test connection.
        // Tunables are conservative: 2 req/s, 3 retry-attempts, 14d dry-run
        // retention. All clamps live in ZammadApiClient on read.
        new SettingDefault(SettingKeys.Zammad.Enabled, "false", "bool", "Zammad",
            "Master kill-switch. When false the integration page is read/writable for setup but every Zammad endpoint refuses with 409 so an in-progress dry-run cannot accidentally fire. Off by default so a fresh install is silent until the admin opts in."),
        new SettingDefault(SettingKeys.Zammad.BaseUrl, "", "string", "Zammad",
            "Base URL of the source Zammad instance, e.g. https://desk.example.com. The client appends /api/v1/... to this. Trailing slashes are normalised away on write. Non-HTTPS values are rejected for any non-localhost host so a typo can't route the API token over plaintext."),
        new SettingDefault(SettingKeys.Zammad.PerPageDefault, "50", "int", "Zammad",
            "Page size used when proxying Zammad's /tickets/search and list endpoints. Clamped to [1, 200] on read. Default 50 keeps a result page snappy without round-trip chatter on large filter-matches."),
        new SettingDefault(SettingKeys.Zammad.MaxRequestsPerSecond, "2", "int", "Zammad",
            "Defensive client-side cap on outbound Zammad requests per second. Zammad publishes no rate limit; this prevents a bulk dry-run from hammering a production helpdesk. Clamped to [1, 20] (whole requests per second)."),
        new SettingDefault(SettingKeys.Zammad.RetryBaseSeconds, "2", "int", "Zammad",
            "Base delay (seconds) for exponential backoff between retries on 429 / 5xx. Actual delay is base * 2^attempt plus ±20% jitter. Clamped to [1, 60]."),
        new SettingDefault(SettingKeys.Zammad.RetryMaxAttempts, "3", "int", "Zammad",
            "Maximum retry attempts before a Zammad call surfaces as a hard failure. Clamped to [0, 8]. Default 3 covers a short upstream blip without dragging an admin-facing Test connection out minutes deep."),
        new SettingDefault(SettingKeys.Zammad.DryRunRetentionDays, "14", "int", "Zammad",
            "Retention window (days) for dry-run snapshots in zammad_import_runs. Dry-run payloads carry the full per-ticket mapping verdict + frozen id-list and are heavy on large migrations, so the sweeper retires them aggressively. Clamped to [1, 90]. Default 14."),
        new SettingDefault(SettingKeys.Zammad.SelectAllMatchingHardCap, "20000", "int", "Zammad",
            "Hard cap on tickets walked per \"Select all matching\" dry-run / import. Stops a runaway filter from blocking the worker for hours on a free-text query that accidentally matches the whole upstream. Clamped to [100, 200000]. Default 20000 — generous enough for full-customer migrations while still preventing a worst-case 1M-ticket walk."),

        // Integrations — v0.0.25 healthcheck framework. Cross-integration
        // knobs only; per-connector specifics live under their own
        // section (Adsolut.*, Graph.*). Defaults tuned for "low load on
        // upstream IdPs, fast enough heartbeat for the SPA": tick every
        // 5 minutes, actively probe the refresh token every 12 hours.
        new SettingDefault(SettingKeys.Integrations.HealthcheckIntervalSeconds, "300", "int", "Integrations",
            "How often (seconds) the integrations healthcheck worker ticks. Each tick reads connection state for every configured integration, writes a heartbeat row to integration_audit, and pushes the resolved status to admins over SignalR. Floor 60 — set lower and the worker silently clamps. Default 300 = a 5-minute heartbeat which is well below the SPA's 30-second poll fallback."),
        new SettingDefault(SettingKeys.Integrations.HealthcheckActiveProbeHours, "12", "int", "Integrations",
            "Hours between active refresh probes by the healthcheck worker. An active probe calls the upstream token endpoint to verify the refresh token still works (catches a revoked RT before the admin finds out via a failing API call). Default 12 keeps Wolters Kluwer load minimal while still surfacing revocation within half a day. Floor 1 hour."),
        new SettingDefault(SettingKeys.Integrations.AuditRetentionHours, "24", "int", "Integrations",
            "Retention window (hours) for the high-volume operational rows in integration_audit — the Telavox per-tick poll summary (capi.calls.poll) and the Adsolut healthcheck heartbeat (healthcheck.tick). Admin-action rows (partner-token updated, agent provisioned/revoked, …) and rare PAPI calls (customers list, api-user create/delete, …) are NEVER swept. The sweep runs once per healthcheck tick. Default 24 (one day). Floor 1 hour."),


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

        // Maintenance-window banner. Off by default. Stored as ISO-8601 UTC
        // strings so both server and client treat them as canonical instants;
        // empty start/end + Enabled=true is allowed (banner shows immediately
        // with no auto-expiry — admin must flip the toggle).
        new SettingDefault(SettingKeys.App.MaintenanceEnabled, "false", "bool", "App",
            "When true, a maintenance-warning banner shows app-wide and on the login page. The banner appears as soon as the toggle flips on — start time is informational. After the end time passes the server reports the window as inactive without changing the toggle, so admins can re-use the same window."),
        new SettingDefault(SettingKeys.App.MaintenanceStartUtc, "", "string", "App",
            "ISO-8601 UTC timestamp of the planned maintenance start. Shown in the banner copy. Not used to gate the banner — visibility is driven by Enabled + EndUtc."),
        new SettingDefault(SettingKeys.App.MaintenanceEndUtc, "", "string", "App",
            "ISO-8601 UTC timestamp at which the banner auto-disappears. Empty = banner stays up until an admin disables the toggle."),
        new SettingDefault(SettingKeys.App.MaintenanceMessage, "", "string", "App",
            "Free-text message shown inside the maintenance banner. Empty falls back to a generic 'service may be temporarily affected' line."),

        // Login banner — admin-controlled notice on every anonymous auth
        // page (local login + M365 callback + future customer-portal login).
        // Separate from the maintenance banner so admins can post a generic
        // info/warning/error notice without touching the maintenance toggle.
        new SettingDefault(SettingKeys.App.LoginBannerEnabled, "false", "bool", "App",
            "When true, a notice banner is shown above the login card on every anonymous auth page. The banner is read via a public endpoint, so it works before authentication. Disable to hide it again — content is preserved across toggles."),
        new SettingDefault(SettingKeys.App.LoginBannerType, "info", "string", "App",
            "Visual style of the login banner. One of 'info' (blue, neutral notice), 'warning' (amber, attention needed) or 'error' (red, operational issue). Unknown values silently fall back to 'info'."),
        new SettingDefault(SettingKeys.App.LoginBannerMessage, "", "string", "App",
            "Body text of the login banner. Supports a constrained Markdown subset (bold, italic, links) which is sanitized to HTML on the client. Empty or whitespace-only messages suppress rendering even when the toggle is on."),

        // Notifications — v0.0.12 stap 4. Mention-trigger notification
        // raamwerk (@@-tag pipeline). Per-user preferences are out of scope
        // for this release — the global kill-switch covers the immediate
        // "too-noisy" case until we know what fine-grained control installs
        // actually want.
        new SettingDefault(SettingKeys.Notifications.MentionEmailEnabled, "true", "bool", "Notifications",
            "When true, a tagged agent receives an email from the ticket's queue mailbox on top of the in-app toast + navbar entry. Turn off on installs where the in-app channel is sufficient."),
        new SettingDefault(SettingKeys.Notifications.PopupDurationSeconds, "10", "int", "Notifications",
            "How long (seconds) the mention pop-up toast stays on screen before auto-dismissing. The navbar entry and history page are unaffected."),

        // Activity feed — v0.0.42. Single global retention window;
        // per-user opt-in lives on the users row, not in settings.
        new SettingDefault(SettingKeys.ActivityFeed.RetentionDays, "365", "int", "ActivityFeed",
            "How long (days) rows in agent_activity_events are kept before the prune worker hard-deletes them. Default 365 = one year. Clamped to [7, 3650] on read; values below 7 days defeat the purpose of an audit trail, values above ~10 years bloat the table without practical benefit."),
        new SettingDefault(SettingKeys.ActivityFeed.PruneIntervalHours, "24", "int", "ActivityFeed",
            "How often (hours) the prune worker runs the retention sweep. Default 24 = once per day. Clamped to [1, 168]."),

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
        new SettingDefault(SettingKeys.IntakeForms.AutoPinSubmittedForms, "true", "bool", "IntakeForms",
            "When true, an intake-form submission is automatically pinned in the ticket activity feed so the agent sees it at the top. Pin can still be removed manually. Turn off if your team prefers to triage submissions chronologically without surfacing them above other pinned context."),

        // Surveys — v0.0.38 CSAT. TtlDays default mirrors the user's stated
        // 7-day expectation but each survey can override in the designer.
        // RateLimit is intentionally stricter than IntakeForms (10/min vs
        // 20/min) because a survey GET-POST is cheaper traffic and bigger
        // value to a brute-force enumeration.
        new SettingDefault(SettingKeys.Surveys.DefaultTtlDays, "7", "int", "Surveys",
            "Default validity window (days) for a newly sent survey link. Used when the survey's designer-level TTL is left blank. Existing invitations keep their original expires_utc."),
        new SettingDefault(SettingKeys.Surveys.ExpirySweepMinutes, "15", "int", "Surveys",
            "How often (minutes) the background worker flips Sent → Expired for invitations past their expires_utc and writes a SurveyExpired ticket event."),
        new SettingDefault(SettingKeys.Surveys.EnableAgentNotifications, "true", "bool", "Surveys",
            "When true, the agents being rated receive an in-app notification via the @-mention framework as soon as a customer submits a survey. Turn off if your team finds the notifications noisy."),
        new SettingDefault(SettingKeys.Surveys.InviteFromName, "", "string", "Surveys",
            "Optional display-name override for the From: field on survey invitation emails. Leave blank to use the default mailbox display name."),
        new SettingDefault(SettingKeys.Surveys.PublicRateLimitPermits, "10", "int", "Surveys",
            "Requests permitted per rate-limit window against the public /api/public/surveys/{token} endpoints, partitioned by {ip,token}."),
        new SettingDefault(SettingKeys.Surveys.PublicRateLimitWindowSeconds, "60", "int", "Surveys",
            "Rate-limit window length (seconds) for the public survey endpoints."),
        new SettingDefault(SettingKeys.Surveys.MaxQuestionsPerSurvey, "30", "int", "Surveys",
            "Maximum number of questions allowed per survey. Enforced server-side on save; bigger surveys mean fewer completed responses."),
        new SettingDefault(SettingKeys.Surveys.MaxCommentLength, "4000", "int", "Surveys",
            "Hard cap (characters) on the optional free-text comment field a customer can submit alongside the structured answers."),

        // Knowledge Base public links — v0.0.75. Disabled by default;
        // the anonymous reader endpoints return 404 for everything until
        // an admin flips the switch (Settings → Knowledge Base).
        new SettingDefault(SettingKeys.KnowledgeBase.PublicLinksEnabled, "false", "bool", "KnowledgeBase",
            "When true, Published Knowledge Base articles are readable without login via /kb/public/{id} links (e.g. pasted into outbound mail). Draft/Internal/Archived articles are never served publicly. When false, the public endpoints return 404 for everything."),
        new SettingDefault(SettingKeys.KnowledgeBase.PublicRateLimitPermits, "60", "int", "KnowledgeBase",
            "Requests permitted per rate-limit window against the public /api/public/kb endpoints, partitioned by {ip,article}. Higher than the survey limit because an article page also fetches its inline images."),
        new SettingDefault(SettingKeys.KnowledgeBase.PublicRateLimitWindowSeconds, "60", "int", "KnowledgeBase",
            "Rate-limit window length (seconds) for the public Knowledge Base endpoints."),

        // ISO 27001 workflow — v0.0.40. Single queue-binding setting; when
        // empty the classification buttons never appear and the feature is
        // dormant. Admin populates this after creating the dedicated ISO
        // queue and setting up the manual trigger that auto-routes intake.
        new SettingDefault(SettingKeys.Iso27001.QueueId, "", "guid", "Iso27001",
            "Queue id where the ISO 27001 workflow runs. Empty = feature disabled, no classification buttons appear. Set this to the id of your dedicated ISO 27001 queue (Settings → Tickets → Queues) to enable the MGM → DPO flow."),

        // Triggers — v0.0.24. Loop-prevention knobs for the evaluator. The
        // chain-cap stops a trigger storm where trigger A's side-effects
        // re-arm trigger B which re-arms A. The mail dedup window prevents
        // the same trigger from mailing the same ticket twice when an agent
        // makes several rapid edits that all match the same condition.
        new SettingDefault(SettingKeys.Triggers.MaxChainPerMutation, "10", "int", "Triggers",
            "Hard cap on the number of trigger evaluations chained off a single ticket mutation. A trigger whose actions re-match other triggers stops escalating once this cap is reached and the chain is logged with outcome='skipped_loop'."),
        new SettingDefault(SettingKeys.Triggers.MailDedupWindowMinutes, "5", "int", "Triggers",
            "Within this rolling window (minutes), a trigger that already sent a mail for a given ticket suppresses repeat sends from the same trigger. Prevents spam when an agent rapidly toggles fields that all match the same trigger condition."),
        new SettingDefault(SettingKeys.Triggers.SchedulerIntervalSeconds, "60", "int", "Triggers",
            "How often (seconds) the time-trigger scheduler scans for tickets whose pending-till or SLA deadline has elapsed. Lower values reduce latency but raise DB load; the floor is 15 seconds. The default of 60 mirrors a 1-minute tick which is fine-grained enough for any helpdesk SLA."),
        new SettingDefault(SettingKeys.Triggers.EscalationWarningMinutes, "30", "int", "Triggers",
            "How many minutes before the SLA deadline an 'escalation_warning' trigger fires (e.g. 30 = warn 30 minutes before breach). Has no effect on triggers using the 'reminder' or 'escalation' modes."),

        // Timesheet — v0.0.35-E. Global defaults that drive the Tab-1
        // start-prefill and the Tab-3 target colour-coding. Every Timesheet
        // user starts on these values; an admin can override per user via
        // the timesheet_* columns on users (NULL there = use these defaults).
        new SettingDefault(SettingKeys.Timesheet.DefaultDayStartMinutes, "510", "int", "Timesheet",
            "Start time pre-fill (minutes since midnight) for the first new row of a day on Tab 1. 510 = 08:30. Per-user overrides on the user-edit page take precedence; an empty per-user value falls back to this default."),
        new SettingDefault(SettingKeys.Timesheet.DefaultTargetMinutesPerDay, "480", "int", "Timesheet",
            "Target work-minutes per work-day used by the Tab-3 colour-coding (Under / On / Over). 480 = 8h. Absence-minutes count toward the target so a full Verlof-day shows as 'On target'."),
        new SettingDefault(SettingKeys.Timesheet.DefaultTargetMinutesPerWeek, "2400", "int", "Timesheet",
            "Target work-minutes per ISO-week used by the Tab-3 week-subtotal row. 2400 = 40h."),
        new SettingDefault(SettingKeys.Timesheet.DefaultWorkDays, "1,2,3,4,5", "string", "Timesheet",
            "Comma-separated ISO weekday numbers (1=Mon..7=Sun) counted as work-days. A day not in this set is shown muted in Tab 3 and never flagged as 'Not filled'. Default 1,2,3,4,5 (Mon–Fri)."),
        new SettingDefault(SettingKeys.Timesheet.DefaultMaxAbsenceMinutesPerDay, "30", "int", "Timesheet",
            "Daily ceiling on absence-task minutes (Verlof, Ziek, …) before the ISO-week is flagged 'target not met' in Tab 3, regardless of total time logged. 0 = no ceiling. Default 30 (≈2.5h per 5-day work week)."),
        new SettingDefault(SettingKeys.Timesheet.DefaultOfficeStartMinutes, "510", "int", "Timesheet",
            "Office-hours start (minutes since midnight). Tab 1 flags a row red when its start time doesn't connect to the previous row's end AND the mismatch falls inside the office window. 510 = 08:30."),
        new SettingDefault(SettingKeys.Timesheet.DefaultOfficeEndMinutes, "1020", "int", "Timesheet",
            "Office-hours end (minutes since midnight). 1020 = 17:00."),

        // Timesheet reply-template — v0.0.35-F. The "Import registered time"
        // button on the reply editor concatenates header + (row repeated) +
        // footer with row placeholders substituted server-side. Defaults
        // emit a clean HTML table with inline styles so it renders in any
        // mail client without depending on the install's CSS.
        new SettingDefault(SettingKeys.Timesheet.ReplyHeaderHtml,
            "<p>Time logged on this ticket:</p>\n<table style=\"border-collapse:collapse;width:100%;font-family:Arial,Helvetica,sans-serif;font-size:13px;\"><thead><tr style=\"background:#f3f4f6;\"><th style=\"padding:6px 10px;text-align:left;border:1px solid #e5e7eb;\">Date</th><th style=\"padding:6px 10px;text-align:left;border:1px solid #e5e7eb;\">Start</th><th style=\"padding:6px 10px;text-align:left;border:1px solid #e5e7eb;\">End</th><th style=\"padding:6px 10px;text-align:left;border:1px solid #e5e7eb;\">Description</th><th style=\"padding:6px 10px;text-align:right;border:1px solid #e5e7eb;\">Duration</th></tr></thead><tbody>",
            "string", "Timesheet",
            "HTML emitted once at the top of the reply-template output. Default is the opening <table> with a header row. Admin-edit to match house style; row data is HTML-escaped at render time so a description cannot break out."),
        new SettingDefault(SettingKeys.Timesheet.ReplyRowHtml,
            "<tr><td style=\"padding:6px 10px;border:1px solid #e5e7eb;\">{{date}}</td><td style=\"padding:6px 10px;border:1px solid #e5e7eb;\">{{start}}</td><td style=\"padding:6px 10px;border:1px solid #e5e7eb;\">{{end}}</td><td style=\"padding:6px 10px;border:1px solid #e5e7eb;\">{{description}}</td><td style=\"padding:6px 10px;border:1px solid #e5e7eb;text-align:right;\">{{duration}}</td></tr>",
            "string", "Timesheet",
            "HTML emitted once per timesheet entry. Placeholders: {{date}}, {{start}}, {{end}}, {{duration}}, {{minutes}}, {{description}}, {{agent}}, {{task}}. All placeholder values are HTML-escaped before substitution."),
        new SettingDefault(SettingKeys.Timesheet.ReplyFooterHtml,
            "</tbody><tfoot><tr style=\"background:#f9fafb;font-weight:600;\"><td style=\"padding:6px 10px;border:1px solid #e5e7eb;\" colspan=\"4\">Total ({{count}} entries)</td><td style=\"padding:6px 10px;border:1px solid #e5e7eb;text-align:right;\">{{total_duration}}</td></tr></tfoot></table>",
            "string", "Timesheet",
            "HTML emitted once after the rows. Placeholders: {{total_duration}}, {{total_minutes}}, {{total_hours}}, {{count}}."),
        // v0.0.54 — migration import master switch. Off by default; the
        // secret-gated import surface stays invisible until an admin enables
        // it AND configures the import token.
        new SettingDefault(SettingKeys.Timesheet.ImportEnabled, "false", "bool", "Timesheet",
            "Master switch for the one-time migration import surface (Settings → Timesheet → Migration import). When off, the import endpoints return 404."),
        new SettingDefault(SettingKeys.Timesheet.HourlyRate, "0", "decimal", "Timesheet",
            "Gross hourly rate in EUR used to price registered hours. Drives the 'Bruto Price' column on the Timesheet → Adsolut tab (rate × registered hours, per receipt and broken down per task). Enter a number like 75 or 75.50 (comma also accepted). 0 leaves the Bruto Price column blank."),
        // v0.0.56 — back-office Resolved / CWI tabs. Which statuses feed
        // each tab is chosen by the admin (by name) under Settings →
        // Timesheet → Back-office tabs. Stored as a CSV of status ids so a
        // later rename keeps the selection; empty = that tab lists nothing.
        new SettingDefault(SettingKeys.Timesheet.ResolvedTabStatusIds, "", "string", "Timesheet",
            "Statuses whose tickets appear on the back-office 'Resolved' tab. A ticket is listed in the month it entered one of these statuses, and only when it has no Adsolut sales receipt yet. Pick one or more statuses by name on the Settings → Timesheet → Back-office tabs panel. Empty = the Resolved tab shows nothing."),
        new SettingDefault(SettingKeys.Timesheet.CwiTabStatusIds, "", "string", "Timesheet",
            "Statuses whose tickets appear on the back-office 'CWI' (Closed Without Invoice) tab. A ticket is listed in the month it entered one of these statuses. Pick one or more statuses by name on the Settings → Timesheet → Back-office tabs panel. Empty = the CWI tab shows nothing."),

        // Statistics — v0.0.69. Status-group definitions for the "Hours by
        // status group" metric. Resolved/CWI reuse the back-office sets above;
        // QFI/WFQ are new. CSV of status ids; empty = that group is omitted.
        new SettingDefault(SettingKeys.Statistics.QfiStatusIds, "", "string", "Timesheet",
            "Statuses that make up the 'QFI' group in the Statistics 'Hours by status group' metric. Pick statuses by name on the Settings → Timesheet → Statistics status groups panel. Empty = the QFI group is omitted from the chart."),
        new SettingDefault(SettingKeys.Statistics.WfqStatusIds, "", "string", "Timesheet",
            "Statuses that make up the 'WFQ' group in the Statistics 'Hours by status group' metric. Pick statuses by name on the Settings → Timesheet → Statistics status groups panel. Empty = the WFQ group is omitted from the chart."),

        // Tactical RMM — v0.0.52. Master switch defaults off so a fresh
        // install is silent until the admin opts in. Sync cadence is
        // settings-driven (no hardcoded magic number) so an ops team can
        // dial it up/down without a redeploy.
        new SettingDefault(SettingKeys.Trmm.Enabled, "false", "bool", "Tactical RMM",
            "Master kill-switch for the Tactical RMM integration. When false the sync worker is dormant and the Assets endpoints refuse with 409. Flip to true once base URL + API key are configured."),
        new SettingDefault(SettingKeys.Trmm.BaseUrl, "", "string", "Tactical RMM",
            "Base URL of the Tactical RMM API (e.g. https://api.trmm.example.com). Clients/sites/agents paths are appended below. Non-https hosts other than localhost are rejected so a typo cannot route the API key over plaintext."),
        new SettingDefault(SettingKeys.Trmm.SyncIntervalMinutes, "15", "int", "Tactical RMM",
            "Background sync cadence (minutes). The worker pulls clients + sites + agents per tick and upserts into the local mirror tables. Clamped to [1, 1440]."),
        new SettingDefault(SettingKeys.Trmm.RequestTimeoutSeconds, "30", "int", "Tactical RMM",
            "HTTP timeout per TRMM API call, in seconds. Clamped to [5, 300]. Lower = fail-fast on a slow upstream; higher = tolerate occasional latency without aborting a sync."),

        // End-of-life data feed — v0.0.52. The Assets page row-tint
        // depends on these knobs (red = expired, amber = within
        // WarnThresholdDays). All four sit under the "Tactical RMM"
        // category so an admin manages OS lifecycle alongside the rest
        // of the RMM integration.
        new SettingDefault(SettingKeys.Eol.Enabled, "true", "bool", "Tactical RMM",
            "When on, a background worker refreshes the Microsoft Windows + Windows Server end-of-life data from endoflife.date and the Assets page tints rows past or near support end. Off = no tint, no chip; the column shows 'Unknown' for every agent."),
        new SettingDefault(SettingKeys.Eol.RefreshIntervalDays, "7", "int", "Tactical RMM",
            "How often (days) the EOL data refresh runs. The endoflife.date registry changes infrequently — 7 is plenty. Clamped to [1, 90]."),
        new SettingDefault(SettingKeys.Eol.WarnThresholdDays, "180", "int", "Tactical RMM",
            "How many days before the EOL date an agent gets the amber 'soon' tint. Default 180 (6 months). Anything past EOL is always red regardless of this value. Clamped to [1, 3650]."),
        new SettingDefault(SettingKeys.Eol.BaseUrl, "https://endoflife.date", "string", "Tactical RMM",
            "Base URL of the endoflife.date API. Change this only for an air-gapped install that mirrors the registry internally. Trailing slashes are normalised."),

        // Email signatures — v0.0.58. Opt-in; with Enabled off nothing is ever
        // appended. Variables fill from Entra ID with a per-user local override.
        new SettingDefault(SettingKeys.Signatures.Enabled, "false", "bool", "Signatures",
            "Master switch for email signatures. When off, no signature is appended on either the agent reply path or the trigger/automated send path. Turn on once you have built and assigned a signature."),
        new SettingDefault(SettingKeys.Signatures.AppendOnReplies, "true", "bool", "Signatures",
            "When on, the resolved signature is also placed on replies (directly under the agent's message, above the quoted history). When off, signatures are only added to the first/new outbound mail of a thread."),
        new SettingDefault(SettingKeys.Signatures.ComposerPreload, "true", "bool", "Signatures",
            "When on, the signature is pre-loaded into the compose window as a fixed, read-only block directly under your message (above the quoted history), and that position is honoured on send. When off, the signature is appended at the very bottom of the mail on send (legacy behaviour)."),
        new SettingDefault(SettingKeys.Signatures.DefaultSystemSignatureId, "", "string", "Signatures",
            "Signature used for trigger/automated mail, where there is no human sender to pull Entra variables from. Pick a signature flagged 'system'. Empty = no signature on automated mail."),
        new SettingDefault(SettingKeys.Signatures.EntraSyncEnabled, "false", "bool", "Signatures",
            "When on, signature variables (job title, mobile, phone) are pulled from Microsoft Entra ID for the sending agent. Requires the Graph app-registration to have User.Read.All (Application) consented. Off = only the per-user local profile fields are used. A Graph failure falls back to the local fields rather than blocking the send."),
        new SettingDefault(SettingKeys.Signatures.EntraSyncPhotos, "false", "bool", "Signatures",
            "When on (and Entra sync is enabled), the {{Photo}} token is filled from the agent's Microsoft Entra ID profile photo. Separate toggle because the photo endpoint is a distinct, heavier Graph call."),
        new SettingDefault(SettingKeys.Signatures.PhotoFrameBlobHash, "", "string", "Signatures",
            "Internal pointer to the admin-uploaded profile-photo frame image (blob hash). Set via the Team profiles photo editor, not by hand. Empty = no frame."),
        new SettingDefault(SettingKeys.Signatures.PhotoFrameMime, "", "string", "Signatures",
            "MIME type of the uploaded profile-photo frame image. Set automatically alongside the frame upload."),
    };
}
