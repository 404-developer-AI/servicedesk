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

namespace Servicedesk.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddServicedeskInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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
        services.AddSingleton<IHealthAggregator, HealthAggregator>();
        services.AddSingleton<IHealthSubsystemReset, HealthSubsystemReset>();
        // Default to the no-op notifier; the Api project overrides this
        // with the SignalR-backed implementation.
        services.AddSingleton<Realtime.ITicketListNotifier, Realtime.NullTicketListNotifier>();
        services.AddSingleton<Realtime.IUserNotifier, Realtime.NullUserNotifier>();

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

        services.AddHostedService<DatabaseBootstrapper>();
        services.AddHostedService<SettingsSeeder>();
        services.AddHostedService<TaxonomySeeder>();
        services.AddHostedService<SlaSeeder>();
        services.AddHostedService<MailPollingService>();
        services.AddHostedService<AttachmentWorker>();
        services.AddHostedService<HolidaySyncWorker>();
        services.AddHostedService<SlaRecalcWorker>();

        return services;
    }
}
