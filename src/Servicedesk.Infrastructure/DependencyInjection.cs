using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Admin;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.DataProtection;
using Servicedesk.Infrastructure.Persistence;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.ViewGroups;
using Servicedesk.Infrastructure.Persistence.Views;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Health.SecurityActivity;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
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

        services.AddSingleton<IAuditLogger, AuditLogger>();
        services.AddSingleton<IAuditQuery, AuditQueryService>();
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

        services.AddSingleton<ITaxonomyRepository, TaxonomyRepository>();
        services.AddSingleton<ICompanyRepository, CompanyRepository>();
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddSingleton<IViewRepository, ViewRepository>();
        services.AddSingleton<IViewGroupRepository, ViewGroupRepository>();
        services.AddSingleton<IQueueAccessService, QueueAccessService>();
        services.AddSingleton<IViewAccessService, ViewAccessService>();

        services.AddSingleton<IBlobStoreHealth, BlobStoreHealth>();
        services.AddSingleton<IBlobStore, LocalFileBlobStore>();

        services.AddSingleton<IIncidentLog, IncidentLog>();
        services.AddHostedService<IncidentLogDrainService>();

        services.AddSingleton<IProtectedSecretStore, ProtectedSecretStore>();
        services.AddSingleton<IMailPollStateRepository, MailPollStateRepository>();
        services.AddSingleton<IGraphMailClient, GraphMailClient>();
        services.AddSingleton<ITlsCertReader, FileTlsCertReader>();
        services.AddSingleton<ICertRenewalTrigger, FileSignalCertRenewalTrigger>();
        services.AddSingleton<ISecurityActivitySnapshot, InMemorySecurityActivitySnapshot>();
        services.AddSingleton<IHealthAggregator, HealthAggregator>();
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
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<TicketSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<ContactSearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<CompanySearchSource>()));
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<SettingsSearchSource>()));
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
        services.AddSingleton<ITriggerConditionMatcher, TriggerConditionMatcher>();
        services.AddSingleton<ITriggerActionDispatcher, TriggerActionDispatcher>();
        services.AddSingleton<TriggerLoopGuard>();
        services.AddSingleton<TriggerMailDedupTracker>();
        services.AddSingleton<ITriggerTemplateRenderer, TriggerTemplateRenderer>();
        services.AddSingleton<ITriggerRenderContextFactory, TriggerRenderContextFactory>();
        services.AddSingleton<ITriggerService, TriggerService>();

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
        services.AddSingleton<ITriggerActionHandler, SendMailHandler>();

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
        services.AddSingleton<ITriggerActionPreviewer, SendMailPreviewer>();

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

        // Triggers search-source (v0.0.24 Blok 8). Admin-only — agents and
        // customers see zero hits, enforced both inside the source and
        // again by the ScopedSearchSource decorator.
        services.AddSingleton<TriggerSearchSource>();
        services.AddSingleton<ISearchSource>(sp => new ScopedSearchSource(sp.GetRequiredService<TriggerSearchSource>()));

        services.AddHostedService<DatabaseBootstrapper>();
        services.AddHostedService<SettingsSeeder>();
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

        return services;
    }
}
