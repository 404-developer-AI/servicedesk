using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// Boots the real Servicedesk host but replaces Postgres-bound services with
/// in-memory fakes so tests can run without a database. The rate limiter, CSP,
/// security headers and all middleware are real — those are what we test.
public sealed class SecurityBaselineFactory : WebApplicationFactory<Program>
{
    public readonly FakeAuditLogger Audit = new();
    public readonly FakeAuditQuery AuditQuery = new();

    private readonly Dictionary<string, string?> _overrides = new()
    {
        ["ConnectionStrings:Postgres"] = "Host=localhost;Database=servicedesk_test_stub;Username=stub;Password=stub",
        ["Audit:HashKey"] = "dGVzdC1rZXktZm9yLWNpLW9ubHktbm90LXNlY3JldA==",
        ["Security:RateLimit:Global:PermitPerWindow"] = "1000",
        ["Security:RateLimit:Global:WindowSeconds"] = "60",
    };

    public SecurityBaselineFactory WithConfig(string key, string? value)
    {
        _overrides[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(_overrides);
        });

        builder.ConfigureServices(services =>
        {
            // Strip DB-backed hosted services.
            services.RemoveAll<IHostedService>();
            // Keep the secret validator (it only reads config).
            services.AddHostedService<Servicedesk.Infrastructure.Secrets.StartupSecretValidator>();

            // Remove NpgsqlDataSource so nothing tries to connect.
            var dsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(NpgsqlDataSource));
            if (dsDescriptor is not null) services.Remove(dsDescriptor);

            // Replace audit + settings services with fakes.
            services.RemoveAll<IAuditLogger>();
            services.AddSingleton<IAuditLogger>(Audit);

            services.RemoveAll<IAuditQuery>();
            services.AddSingleton<IAuditQuery>(AuditQuery);

            services.RemoveAll<ISettingsService>();
            services.AddSingleton<ISettingsService, FakeSettingsService>();
        });
    }
}

internal sealed class FakeSettingsService : ISettingsService
{
    public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult(default(T)!);
    public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
}
