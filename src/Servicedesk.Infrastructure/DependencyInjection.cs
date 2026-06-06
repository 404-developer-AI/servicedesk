using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Auth.Admin;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Dashboard;
using Servicedesk.Infrastructure.DataProtection;
using Servicedesk.Infrastructure.Persistence;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Activity;
using Servicedesk.Infrastructure.Persistence.ViewGroups;
using Servicedesk.Infrastructure.Persistence.Views;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Health.SecurityActivity;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Integrations;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Integrations.Zammad;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Phones;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
using Servicedesk.Infrastructure.Surveys;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Servicedesk.Infrastructure.Triggers.Actions.Previewers;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddServicedeskInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<Health.TlsCertHealthOptions>(configuration.GetSection("TlsCert"));

        services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();

        // StartupSecretValidator must run before DataProtectionHostedService
        // (registered by AddServicedeskDataProtection below) so a missing
        // master key produces the clean aggregated validator error instead of
        // an InvalidOperationException from mid-key-ring warmup. Hosted
        // services run in registration order.
        services.AddHostedService<StartupSecretValidator>();

        services.AddServicedeskDataProtection(configuration);

        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var connectionString = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Postgres is not configured. Set it via environment variable or user-secrets.");
            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });

        // v0.0.42 — IAuditLogger is decorated so every whitelisted audit
        // event also lands in the agent activity feed. The decorator
        // forwards to AuditLogger (the real writer) so the HMAC chain
        // stays intact; the activity-feed tee-off is best-effort.
        services.AddSingleton<AuditLogger>();
        services.AddSingleton<IAuditLogger>(sp => new ActivityLoggingAuditDecorator(
            sp.GetRequiredService<AuditLogger>(),
            sp.GetRequiredService<IActivityRecorder>(),
            sp.GetRequiredService<IUserService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ActivityLoggingAuditDecorator>>()));
        services.AddSingleton<IAuditQuery, AuditQueryService>();

        // Integration-audit (v0.0.25). Operational log for outbound
        // integration calls — distinct from the security-grade audit_log.
        // Same instance serves writer + query because the surface is small.
        services.AddSingleton<IntegrationAuditLogger>();
        services.AddSingleton<IIntegrationAuditLogger>(sp => sp.GetRequiredService<IntegrationAuditLogger>());
        services.AddSingleton<IIntegrationAuditQuery>(sp => sp.GetRequiredService<IntegrationAuditLogger>());
        services.AddSingleton<ISettingsService, SettingsService>();

        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<ITotpService, TotpService>();

        // M365 login (v0.0.13). GraphDirectoryClient reads accountEnabled
        // for the deprovisioning check; MicrosoftAuthService wraps the
        // full OIDC challenge + callback flow.
        services.AddSingleton<IGraphDirectoryClient, GraphDirectoryClient>();
        services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();
        services.AddSingleton<IUserAdminService, UserAdminService>();
        services.AddSingleton<IDashboardTilesService, DashboardTilesService>();
        services.AddSingleton<IAgentActivityService, AgentActivityService>();

        // v0.0.42 — Agent activity feed (append-only audit trail across
        // every agent / admin action). Recorder = direct-write path used
        // by non-ticket subsystems; ticket coverage comes from a Postgres
        // trigger on ticket_events. ListenerWorker subscribes to the
        // 'agent_activity_event' channel and pushes enriched rows to
        // SignalR via IActivityBroadcaster (registered in the API
        // project). RetentionWorker prunes daily.
        services.AddSingleton<IActivityRecorder, ActivityRecorder>();
        services.AddSingleton<IActivityFeedQuery, ActivityFeedQuery>();
        services.AddHostedService<ActivityListenerWorker>();
        services.AddHostedService<ActivityRetentionWorker>();
        // IAgentActivityBroadcaster is registered in the API project
        // (Program.cs) because the implementation lives there — it needs
        // IHubContext<TicketPresenceHub> and the hub's static presence
        // map, both of which live in Api, not Infrastructure.

        // Adsolut OAuth integration (v0.0.25). One install ↔ one Adsolut
        // administration. Refresh-token rotation + sliding-month window
        // tracked in adsolut_connection; secrets in protected_secrets.
        services.AddSingleton<IAdsolutConnectionStore, AdsolutConnectionStore>();
        services.AddSingleton<IAdsolutAuthService, AdsolutAuthService>();
        services.AddHttpClient(AdsolutAuthService.HttpClientName);

        // v0.0.26 — API clients for Adsolut /adm and /acc. Access-token
        // provider caches the bearer 5 minutes so the sync worker doesn't
        // hit the WK token endpoint on every paged Customers call.
        services.AddSingleton<IAdsolutAccessTokenProvider, AdsolutAccessTokenProvider>();
        services.AddSingleton<AdsolutHttpInvoker>();
        services.AddSingleton<IAdsolutAdministrationsClient, AdsolutAdministrationsClient>();
        services.AddSingleton<IAdsolutCustomersClient, AdsolutCustomersClient>();
        services.AddSingleton<IAdsolutCustomersWriteClient, AdsolutCustomersWriteClient>();
        services.AddSingleton<IAdsolutContactsClient, AdsolutContactsClient>();
        services.AddSingleton<IAdsolutContactsWriteClient, AdsolutContactsWriteClient>();
        services.AddSingleton<IAdsolutSyncStateStore, AdsolutSyncStateStore>();
        services.AddSingleton<IAdsolutCompanyUpserter, AdsolutCompanyUpserter>();
        services.AddSingleton<IAdsolutContactUpserter, AdsolutContactUpserter>();
        services.AddSingleton<IAdsolutCompanyPusher, AdsolutCompanyPusher>();
        services.AddSingleton<IAdsolutContactPusher, AdsolutContactPusher>();
        services.AddSingleton<IAdsolutContactsReconciler, AdsolutContactsReconciler>();
        services.AddSingleton<IAdsolutCoverageQuery, AdsolutCoverageQuery>();
        services.AddSingleton<IAdsolutSyncWorkerSignal, AdsolutSyncWorkerSignal>();
        services.AddHttpClient(AdsolutHttpInvoker.HttpClientName);
        services.AddHostedService<AdsolutSyncWorker>();
        services.AddHostedService<AdsolutContactsReconcileWorker>();

        // ERP SalesReceipts (verkoopbonnen) mirror → Timesheet → Adsolut tab.
        // Opt-in via Adsolut.Erp.SalesReceipts.Enabled; reuses the shared
        // AdsolutHttpInvoker (bearer + once-on-401 + audit).
        services.AddSingleton<IAdsolutSalesReceiptsClient, AdsolutSalesReceiptsClient>();
        services.AddSingleton<IAdsolutSalesReceiptRepository, AdsolutSalesReceiptRepository>();
        services.AddSingleton<IAdsolutSalesReceiptsSyncSignal, AdsolutSalesReceiptsSyncSignal>();
        services.AddHostedService<AdsolutSalesReceiptsSyncWorker>();

        // ERP Orders (bestellingen) mirror → Orders overview (navbar → Assets).
        // Opt-in via Adsolut.Erp.Orders.Enabled; reuses the shared
        // AdsolutHttpInvoker. The OrderInfos list returns full orders incl.
        // lines, so the sync upserts straight from the list (no by-id N+1).
        services.AddSingleton<IAdsolutOrdersClient, AdsolutOrdersClient>();
        services.AddSingleton<IAdsolutSupplierOrdersClient, AdsolutSupplierOrdersClient>();
        services.AddSingleton<IAdsolutWarehousesClient, AdsolutWarehousesClient>();
        services.AddSingleton<IAdsolutOrderRepository, AdsolutOrderRepository>();
        services.AddSingleton<IAdsolutOrdersSyncSignal, AdsolutOrdersSyncSignal>();
        services.AddHostedService<AdsolutOrdersSyncWorker>();

        // Telavox call-popup integration (v0.0.34). PAPI partner-token is
        // shared install-wide; CAPI tokens are auto-provisioned per linked
        // agent via /api/admin/integrations/telavox/agents/{userId}/provision
        // and stored in protected_secrets. The polling worker (commit C)
        // reads telavox_agent_links + telavox_call_state, runs each link
        // through the pure transition module, and broadcasts via
        // ITelavoxCallNotifier — defaulted to the null implementation; the
        // Api project overrides it with the SignalR-backed pusher.
        services.AddSingleton<ITelavoxApiClient, TelavoxApiClient>();
        services.AddSingleton<ITelavoxAgentLinkStore, TelavoxAgentLinkStore>();
        services.AddSingleton<ITelavoxCallStateStore, TelavoxCallStateStore>();
        services.AddSingleton<ITelavoxProvisioningService, TelavoxProvisioningService>();
        services.AddSingleton<Realtime.ITelavoxCallNotifier, Realtime.NullTelavoxCallNotifier>();
        services.AddHttpClient(TelavoxApiClient.HttpClientName);
        services.AddHostedService<TelavoxPollingWorker>();

        // Zammad migration link (v0.0.41). One install-wide HTTP token +
        // base URL; no background worker in fase 1 — the integration is
        // admin-driven (Test connection click). Fasen 2-5 add ticket-
        // picker proxies, dry-run resolver and import service against this
        // same client.
        services.AddSingleton<IZammadApiClient, ZammadApiClient>();
        services.AddHttpClient(ZammadApiClient.HttpClientName);
        // v0.0.41 phase 3 — mapping service (Zammad group/state/priority
        // → local queue/status/priority) + dry-run engine. Mapping service
        // is stateless beyond DB I/O; dry-run worker is a singleton
        // hosted service that pulls jobs from an in-memory channel.
        services.AddSingleton<IZammadMappingService, ZammadMappingService>();
        services.AddSingleton<IZammadTicketResolver, ZammadTicketResolver>();
        services.AddSingleton<IZammadDryRunQueue, ZammadDryRunQueue>();
        services.AddSingleton<IZammadDryRunService, ZammadDryRunService>();
        services.AddSingleton<IZammadImportWriter, ZammadImportWriter>();
        services.AddHostedService<ZammadDryRunWorker>();

        // v0.0.43 — Zammad Knowledge Base import. Reuses the same
        // IZammadApiClient + integration_audit pipeline; separate
        // worker so the KB-import lifecycle doesn't share status state
        // with the ticket-side importer.
        services.AddSingleton<IZammadKbImportQueue, ZammadKbImportQueue>();
        services.AddSingleton<IZammadKbImportService, ZammadKbImportService>();
        services.AddHostedService<ZammadKbImportWorker>();

        // Tactical RMM integration (v0.0.52). One install-wide API key +
        // base URL; background poller refreshes the local mirror tables
        // on the cadence configured in Settings. Notifier defaults to a
        // no-op; the Api project overrides it with a SignalR broadcaster
        // on TicketPresenceHub.
        services.AddSingleton<Servicedesk.Infrastructure.Integrations.Trmm.ITrmmApiClient,
            Servicedesk.Infrastructure.Integrations.Trmm.TrmmApiClient>();
        services.AddHttpClient(Servicedesk.Infrastructure.Integrations.Trmm.TrmmApiClient.HttpClientName);
        services.AddSingleton<Servicedesk.Infrastructure.Integrations.Trmm.ITrmmSyncService,
            Servicedesk.Infrastructure.Integrations.Trmm.TrmmSyncService>();
        services.AddSingleton<Servicedesk.Infrastructure.Integrations.Trmm.ITrmmSyncNotifier,
            Servicedesk.Infrastructure.Integrations.Trmm.NullTrmmSyncNotifier>();
        services.AddSingleton<Servicedesk.Infrastructure.Integrations.Trmm.IAssetRepository,
            Servicedesk.Infrastructure.Integrations.Trmm.AssetRepository>();
        services.AddHostedService<Servicedesk.Infrastructure.Integrations.Trmm.TrmmSyncWorker>();

        // EOL data feed (v0.0.52) — pulls endoflife.date weekly into the
        // local eol_releases mirror so the Assets page can tint rows
        // past or near end-of-support without a live API call on render.
        services.AddSingleton<Servicedesk.Infrastructure.Eol.IEolDataClient,
            Servicedesk.Infrastructure.Eol.EolDataClient>();
        services.AddHttpClient(Servicedesk.Infrastructure.Eol.EolDataClient.HttpClientName);
        services.AddSingleton<Servicedesk.Infrastructure.Eol.IEolDataRefreshService,
            Servicedesk.Infrastructure.Eol.EolDataRefreshService>();
        services.AddHostedService<Servicedesk.Infrastructure.Eol.EolDataRefreshWorker>();

        // Healthcheck-BackgroundService writes integration_audit rows and
        // pushes resolved status over SignalR. The notifier defaults to
        // the no-op implementation; the Api project overrides it with
        // the SignalR-backed broadcaster (same pattern as the ticket-list
        // and user-notification notifiers above).
        services.AddSingleton<Realtime.IIntegrationStatusNotifier, Realtime.NullIntegrationStatusNotifier>();
        services.AddHostedService<IntegrationsHealthcheckWorker>();

        services.AddSingleton<ITaxonomyRepository, TaxonomyRepository>();
        // Phone-number normalisation (v0.0.34) — used on every contact
        // write so phone_e164 / mobile_phone_e164 stay in sync, and by the
        // ContactSearchSource phone-branch on lookup. Stateless beyond the
        // settings-resolved default country code, registered as singleton.
        services.AddSingleton<IContactPhoneNormalizer, ContactPhoneNormalizer>();

        services.AddSingleton<ICompanyRepository, CompanyRepository>();
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddSingleton<IRecentTicketsService, RecentTicketsService>();
        services.AddSingleton<IViewRepository, ViewRepository>();
        services.AddSingleton<IViewGroupRepository, ViewGroupRepository>();
        services.AddSingleton<IQueueAccessService, QueueAccessService>();
        services.AddSingleton<IViewAccessService, ViewAccessService>();

        services.AddSingleton<IBlobStoreHealth, BlobStoreHealth>();
        services.AddSingleton<IBlobStore, LocalFileBlobStore>();

        services.AddSingleton<IIncidentLog, IncidentLog>();
        services.AddHostedService<IncidentLogDrainService>();

        services.AddSingleton<IProtectedSecretStore, ProtectedSecretStore>();
        services.AddSingleton<IQueueInboundMailboxRepository, QueueInboundMailboxRepository>();
        services.AddSingleton<IGraphMailClient, GraphMailClient>();
        services.AddSingleton<ITlsCertReader, FileTlsCertReader>();
        services.AddSingleton<ICertRenewalTrigger, FileSignalCertRenewalTrigger>();
        services.AddSingleton<ISecurityActivitySnapshot, InMemorySecurityActivitySnapshot>();
        services.AddSingleton<IHealthAggregator, HealthAggregator>();
        services.AddSingleton<IIntegrationsHealthAggregator, IntegrationsHealthAggregator>();
        services.AddSingleton<IHealthSubsystemReset, HealthSubsystemReset>();
        // Default to the no-op notifiers; the Api project overrides these
        // with the SignalR-backed implementations.
        services.AddSingleton<Realtime.ITicketListNotifier, Realtime.NullTicketListNotifier>();
        services.AddSingleton<Realtime.IUserNotifier, Realtime.NullUserNotifier>();
        services.AddSingleton<Realtime.ISecurityAlertNotifier, Realtime.NullSecurityAlertNotifier>();

        // Mention notification pipeline (v0.0.12 stap 4).
        services.AddSingleton<INotificationRepository, NotificationRepository>();
        services.AddSingleton<IMentionNotificationService, MentionNotificationService>();

        services.AddSingleton<IMailMessageRepository, MailMessageRepository>();
        services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
        services.AddSingleton<IAttachmentJobRepository, AttachmentJobRepository>();

        // Knowledge Base (v0.0.31). Sanitizer is stateless — single instance
        // shared across requests. Repos follow the existing data-access
        // pattern (NpgsqlDataSource + Dapper, registered as singletons).
        services.AddSingleton<KnowledgeBase.IKbHtmlSanitizer, KnowledgeBase.KbHtmlSanitizer>();
        services.AddSingleton<Persistence.KnowledgeBase.IKbConfigRepository, Persistence.KnowledgeBase.KbConfigRepository>();
        services.AddSingleton<Persistence.KnowledgeBase.IKbSectionRepository, Persistence.KnowledgeBase.KbSectionRepository>();
        services.AddSingleton<Persistence.KnowledgeBase.IKbArticleRepository, Persistence.KnowledgeBase.KbArticleRepository>();

        // Timesheet (v0.0.35). TaskService caches the catalogue (rarely
        // changes); EntryService is uncached because each agent's daily
        // grid is hit once per page-load and writes invalidate it
        // implicitly via the next read.
        services.AddSingleton<Timesheet.ITimesheetTaskService, Timesheet.TimesheetTaskService>();
        services.AddSingleton<Timesheet.ITimesheetEntryService, Timesheet.TimesheetEntryService>();
        services.AddSingleton<Timesheet.IManagerTimesheetService, Timesheet.ManagerTimesheetService>();
        services.AddSingleton<Timesheet.ITimesheetPreferencesService, Timesheet.TimesheetPreferencesService>();
        services.AddSingleton<Timesheet.ITicketTimesheetService, Timesheet.TicketTimesheetService>();
        // v0.0.56 — back-office Resolved / CWI tabs backend.
        services.AddSingleton<Timesheet.IBackofficeTimesheetService, Timesheet.BackofficeTimesheetService>();
        // v0.0.54 — migration import surface backend.
        services.AddSingleton<Timesheet.ITimesheetImportService, Timesheet.TimesheetImportService>();
        // Default to the no-op notifier; the Api project overrides this
        // with the SignalR-backed implementation.
        services.AddSingleton<Realtime.ITimesheetEntryNotifier, Realtime.NullTimesheetEntryNotifier>();
        services.AddSingleton<IMailAttachmentDiagnostics, MailAttachmentDiagnostics>();
        services.AddSingleton<IMailTimelineEnricher, MailTimelineEnricher>();
        services.AddSingleton<IMailFinalizer, MailFinalizer>();
        services.AddSingleton<IContactLookupService, ContactLookupService>();
        services.AddSingleton<IMailIngestService, MailIngestService>();
        services.AddSingleton<IOutboundMailService, OutboundMailService>();
        services.AddSingleton<ITicketNumberLookup>(sp =>
            (ITicketNumberLookup)sp.GetRequiredService<ITicketRepository>());

        services.AddHttpClient();

        // Global search. Every source is wrapped in ScopedSearchSource so
        // the principal-availability check runs even if a source forgets it
        // internally. Adding a new source (Companies, Kennisbank, …) means
        // registering the concrete type here and wrapping it in the
        // decorator — nothing else.
        services.AddSingleton<TicketSearchSource>();
        services.AddSingleton<ContactSearchSource>();
        services.AddSingleton<CompanySearchSource>();
        services.AddSingleton<SettingsSearchSource>();
        services.AddSingleton<KbArticleSearchSource>();
        services.AddSingleton<AssetSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<TicketSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<ContactSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<CompanySearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<SettingsSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<KbArticleSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<AssetSearchSource>()));
        services.AddSingleton<ISearchService, SearchService>();

        services.AddSingleton<ISlaRepository, SlaRepository>();
        services.AddSingleton<IBusinessHoursCalculator, BusinessHoursCalculator>();
        services.AddSingleton<ISlaEngine, SlaEngine>();
        services.AddSingleton<IHolidaySyncService, HolidaySyncService>();

        // Triggers (v0.0.24 Blok 2 + Blok 3). The dispatcher resolves
        // IEnumerable<ITriggerActionHandler> via DI; adding a new action
        // kind means registering one more concrete handler below — no
        // other wiring change needed. Loop guard is Singleton so its
        // AsyncLocal counter spans the whole process; the dedup tracker
        // shares IMemoryCache.
        services.AddSingleton<ITriggerRepository, TriggerRepository>();
        services.AddSingleton<ITriggerGroupRepository, TriggerGroupRepository>();
        services.AddSingleton<ITriggerConditionMatcher, TriggerConditionMatcher>();
        services.AddSingleton<ITriggerActionDispatcher, TriggerActionDispatcher>();
        services.AddSingleton<TriggerLoopGuard>();
        services.AddSingleton<TriggerMailDedupTracker>();
        services.AddSingleton<ITriggerTemplateRenderer, TriggerTemplateRenderer>();
        services.AddSingleton<ITriggerRenderContextFactory, TriggerRenderContextFactory>();
        services.AddSingleton<ITriggerService, TriggerService>();
        // v0.0.42 — status-change gate matcher. Called synchronously by
        // the ticket PATCH endpoint and the GET status-gates endpoint.
        // Singleton because the dependencies it composes are themselves
        // long-lived; no per-request state lives on the service.
        services.AddSingleton<Servicedesk.Infrastructure.Triggers.StatusGate.IStatusGateService,
                              Servicedesk.Infrastructure.Triggers.StatusGate.StatusGateService>();

        // Blok 3 action handlers. add_tags / remove_tags are intentionally
        // unregistered until the tags schema + UI land — the dispatcher
        // returns NoHandler with a clear message in trigger_runs so admins
        // see the gap immediately.
        services.AddSingleton<SystemFieldMutator>();
        services.AddSingleton<ITriggerActionHandler, SetQueueHandler>();
        services.AddSingleton<ITriggerActionHandler, SetPriorityHandler>();
        services.AddSingleton<ITriggerActionHandler, SetStatusHandler>();
        services.AddSingleton<ITriggerActionHandler, SetOwnerHandler>();
        services.AddSingleton<ITriggerActionHandler, SetPendingTillHandler>();
        services.AddSingleton<ITriggerActionHandler, AddInternalNoteHandler>();
        services.AddSingleton<ITriggerActionHandler, AddPublicNoteHandler>();
        services.AddSingleton<ITriggerActionHandler, RepostAsPublicReplyHandler>();
        services.AddSingleton<ITriggerActionHandler, SendMailHandler>();
        services.AddSingleton<ITriggerActionHandler, SendSurveyHandler>();

        // v0.0.39 — manual "Create linked X ticket" preset service. Lives
        // outside ITriggerActionHandler because manual triggers run
        // synchronously on an explicit endpoint call rather than via the
        // event-driven evaluator. Stateless beyond the injected repos.
        services.AddSingleton<ILinkedTicketPresetService, LinkedTicketPresetService>();

        // Blok 7 — dry-run previewers: parallel registration, one per
        // action kind. The preview-dispatcher is the only consumer; the
        // production evaluator never sees these. Adding a new action
        // kind requires both a handler AND a previewer here, otherwise
        // the test-runner reports NoHandler for that action.
        services.AddSingleton<ITriggerActionPreviewDispatcher, TriggerActionPreviewDispatcher>();
        services.AddSingleton<ITriggerActionPreviewer, SetQueuePreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, SetPriorityPreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, SetStatusPreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, SetOwnerPreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, SetPendingTillPreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, AddInternalNotePreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, AddPublicNotePreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, RepostAsPublicReplyPreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, SendMailPreviewer>();
        services.AddSingleton<ITriggerActionPreviewer, SendSurveyPreviewer>();

        // Compose templates — pre-canned HTML snippets for note/reply/mail
        // composers. Reuses KbHtmlSanitizer for body sanitisation; the picker
        // endpoint sits next to the intake-form one so both can power the
        // same `::` Tiptap suggestion.
        services.AddSingleton<IComposeTemplateRepository, ComposeTemplateRepository>();
        services.AddSingleton<IComposeTokenResolver, ComposeTokenResolver>();

        // Ticket templates — pre-canned ticket field sets the New-Ticket drawer
        // applies in one click. Reuses the ComposeToken engine for {{tokens}}
        // in subject/body/initial-note and KbHtmlSanitizer for body cleaning.
        services.AddSingleton<TicketTemplates.ITicketTemplateRepository, TicketTemplates.TicketTemplateRepository>();

        // Tagging-only mailboxes — login-less @@-mention targets that receive
        // a notification mail when tagged. Managed admin-only under Users.
        services.AddSingleton<TaggingMailboxes.ITaggingMailboxRepository, TaggingMailboxes.TaggingMailboxRepository>();

        // Email signatures (v0.0.58). Renderer + sanitizer are stateless;
        // repositories follow the NpgsqlDataSource + Dapper pattern. The
        // resolver caches resolved variables in IMemoryCache (invalidated on a
        // profile edit) and lazily caches the Entra photo as a blob. The
        // composer is the send-path entry point used by both OutboundMailService
        // and the trigger SendMailHandler.
        services.AddSingleton<ISignatureHtmlSanitizer, SignatureHtmlSanitizer>();
        services.AddSingleton<ISignatureRenderer, SignatureRenderer>();
        services.AddSingleton<ISignatureRepository, SignatureRepository>();
        services.AddSingleton<IAgentProfileRepository, AgentProfileRepository>();
        services.AddSingleton<ISignatureVariableResolver, SignatureVariableResolver>();
        services.AddSingleton<ISignatureComposer, SignatureComposer>();

        // Intake Forms (v0.0.19)
        services.AddSingleton<IIntakeTemplateRepository, IntakeTemplateRepository>();
        services.AddSingleton<IIntakeFormRepository, IntakeFormRepository>();
        services.AddSingleton<IIntakeFormTokenService, IntakeFormTokenService>();
        services.AddSingleton<IIntakeTokenResolver, IntakeTokenResolver>();
        services.AddSingleton<IIntakeFormPdfBuilder, IntakeFormPdfBuilder>();

        // PdfSharpCore needs a font resolver on every platform (its default
        // is null and throws on MeasureString). The bundled FontResolver
        // searches the OS font directories — works on Windows out of the
        // box and on Linux containers where `fonts-liberation` or similar
        // is installed. Set once per process; GlobalFontSettings is static.
        if (PdfSharpCore.Fonts.GlobalFontSettings.FontResolver is null)
        {
            PdfSharpCore.Fonts.GlobalFontSettings.FontResolver =
                new PdfSharpCore.Utils.FontResolver();
        }
        services.AddSingleton<IntakeTemplateSearchSource>();
        services.AddSingleton<IntakeSubmissionSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<IntakeTemplateSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<IntakeSubmissionSearchSource>()));

        // Surveys (v0.0.38). Token-protected per-send invitations, CRUD on
        // the designer, results aggregation, dispatch service shared by the
        // send_survey trigger action and the compose-template linked-survey
        // hook. Hosted ExpiryWorker runs alongside the intake one.
        services.AddSingleton<ISurveyInviteHtmlSanitizer, SurveyInviteHtmlSanitizer>();
        services.AddSingleton<ISurveyRepository, SurveyRepository>();
        services.AddSingleton<ISurveyInvitationRepository, SurveyInvitationRepository>();
        services.AddSingleton<ISurveyTokenService, SurveyTokenService>();
        services.AddSingleton<ISurveyDispatchService, SurveyDispatchService>();
        services.AddSingleton<ISurveyResponseService, SurveyResponseService>();
        services.AddSingleton<IComposeTemplateSurveyDispatcher, ComposeTemplateSurveyDispatcher>();
        services.AddSingleton<SurveySearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<SurveySearchSource>()));

        // Triggers search-source (v0.0.24 Blok 8). Admin-only — agents and
        // customers see zero hits, enforced both inside the source and
        // again by the ScopedSearchSource decorator.
        services.AddSingleton<TriggerSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<TriggerSearchSource>()));

        // Email-signatures search-source (v0.0.58). Admin-only — agents and
        // customers see zero hits, enforced inside the source and again by the
        // ScopedSearchSource decorator.
        services.AddSingleton<SignatureSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<SignatureSearchSource>()));

        // Timesheet search-source (v0.0.35 commit H). Customer sees zero
        // hits; Agent/Admin see entries scoped by the timesheet_manager
        // flag (read per-query from the users row).
        services.AddSingleton<TimesheetSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<TimesheetSearchSource>()));

        // Adsolut sales-receipts (verkoopbonnen) search-source. Customer sees
        // zero hits; Agent/Admin see hits only when their own
        // adsolut_timesheet_enabled flag is set (read per-query).
        services.AddSingleton<Servicedesk.Infrastructure.Search.AdsolutSalesReceiptSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<Servicedesk.Infrastructure.Search.AdsolutSalesReceiptSearchSource>()));

        // Adsolut orders (bestellingen) search-source. Customer sees zero hits;
        // Agent/Admin see hits only when their own adsolut_orders_enabled flag
        // is set (read per-query). Honors the admin's display status filter.
        services.AddSingleton<Servicedesk.Infrastructure.Search.AdsolutOrderSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<Servicedesk.Infrastructure.Search.AdsolutOrderSearchSource>()));

        services.AddHostedService<DatabaseBootstrapper>();
        services.AddHostedService<SettingsSeeder>();
        // Lazy E.164 backfill for contacts that existed before v0.0.34
        // added the phone_e164 / mobile_phone_e164 columns. Runs after
        // SettingsSeeder so Telavox.DefaultCountryCode is already in the
        // settings table when the normaliser asks for it.
        services.AddHostedService<ContactPhoneBackfillService>();
        services.AddHostedService<TaxonomySeeder>();
        services.AddHostedService<SlaSeeder>();
        services.AddHostedService<TriggerSeeder>();
        services.AddHostedService<MailPollingService>();
        services.AddHostedService<AttachmentWorker>();
        services.AddHostedService<HolidaySyncWorker>();
        services.AddHostedService<SlaRecalcWorker>();
        services.AddHostedService<TriggerSchedulerWorker>();
        services.AddHostedService<SecurityActivityMonitor>();
        services.AddHostedService<IntakeFormExpiryWorker>();
        services.AddHostedService<SurveyExpiryWorker>();

        return services;
    }
}
