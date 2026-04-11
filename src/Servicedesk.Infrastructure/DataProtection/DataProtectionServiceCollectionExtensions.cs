using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Servicedesk.Infrastructure.DataProtection;

public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Wires ASP.NET Data Protection to the Postgres-backed, AES-GCM-encrypted
    /// keyring. Replaces the default filesystem persistence: no keys ever land
    /// on disk, dev or prod. The master key is read from
    /// <c>DataProtection:MasterKey</c> (base64, 32 bytes) at service
    /// construction — <see cref="Secrets.StartupSecretValidator"/> guarantees
    /// it is present before the web host accepts requests.
    /// </summary>
    public static IServiceCollection AddServicedeskDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<PostgresXmlRepository>(sp =>
        {
            var masterKey = ReadMasterKey(configuration);
            return new PostgresXmlRepository(
                sp.GetRequiredService<NpgsqlDataSource>(),
                masterKey,
                sp.GetRequiredService<ILogger<PostgresXmlRepository>>());
        });

        services.AddSingleton<IConfigureOptions<KeyManagementOptions>, ConfigurePostgresKeyring>();

        services.AddDataProtection()
            .SetApplicationName("Servicedesk");

        return services;
    }

    private static byte[] ReadMasterKey(IConfiguration configuration)
    {
        var raw = configuration["DataProtection:MasterKey"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "DataProtection:MasterKey is not configured. " +
                "Generate one with `openssl rand -base64 32` and set it via environment variable.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "DataProtection:MasterKey must be a valid base64 string (32 bytes decoded).", ex);
        }

        if (bytes.Length != 32)
        {
            throw new InvalidOperationException(
                $"DataProtection:MasterKey must decode to 32 bytes (got {bytes.Length}). " +
                "Generate one with `openssl rand -base64 32`.");
        }

        return bytes;
    }

    private sealed class ConfigurePostgresKeyring : IConfigureOptions<KeyManagementOptions>
    {
        private readonly IServiceProvider _serviceProvider;

        public ConfigurePostgresKeyring(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void Configure(KeyManagementOptions options)
        {
            options.XmlRepository = _serviceProvider.GetRequiredService<PostgresXmlRepository>();
        }
    }
}
