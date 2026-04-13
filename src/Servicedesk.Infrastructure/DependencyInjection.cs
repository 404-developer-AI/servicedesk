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
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

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

        services.AddHostedService<DatabaseBootstrapper>();
        services.AddHostedService<SettingsSeeder>();
        services.AddHostedService<TaxonomySeeder>();

        return services;
    }
}
