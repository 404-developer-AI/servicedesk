using Microsoft.Extensions.Hosting;

namespace Servicedesk.Infrastructure.Settings;

/// Runs after <see cref="Servicedesk.Infrastructure.Persistence.DatabaseBootstrapper"/>
/// (hosted services start in registration order) and seeds default setting rows.
public sealed class SettingsSeeder : IHostedService
{
    private readonly ISettingsService _settings;

    public SettingsSeeder(ISettingsService settings)
    {
        _settings = settings;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _settings.EnsureDefaultsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
