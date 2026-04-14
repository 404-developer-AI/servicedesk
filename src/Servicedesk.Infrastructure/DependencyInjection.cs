using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
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
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
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

        services.AddSingleton<IMailMessageRepository, MailMessageRepository>();
        services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
        services.AddSingleton<IAttachmentJobRepository, AttachmentJobRepository>();
        services.AddSingleton<IMailAttachmentDiagnostics, MailAttachmentDiagnostics>();
        services.AddSingleton<IMailTimelineEnricher, MailTimelineEnricher>();
        services.AddSingleton<IMailFinalizer, MailFinalizer>();
        services.AddSingleton<IContactLookupService, ContactLookupService>();
        services.AddSingleton<IMailIngestService, MailIngestService>();
        services.AddSingleton<ITicketNumberLookup>(sp =>
            (ITicketNumberLookup)sp.GetRequiredService<ITicketRepository>());

        services.AddHostedService<DatabaseBootstrapper>();
        services.AddHostedService<SettingsSeeder>();
        services.AddHostedService<TaxonomySeeder>();
        services.AddHostedService<MailPollingService>();
        services.AddHostedService<AttachmentWorker>();

        return services;
    }
}
