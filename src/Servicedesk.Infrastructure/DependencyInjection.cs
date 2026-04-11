using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence;
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

        services.AddHostedService<StartupSecretValidator>();
        services.AddHostedService<DatabaseBootstrapper>();
        services.AddHostedService<SettingsSeeder>();

        return services;
    }
}
