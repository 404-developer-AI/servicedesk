using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// BackgroundService that periodically pulls TRMM clients/sites/agents and
/// refreshes the local mirror tables. Cadence comes from
/// <see cref="SettingKeys.Trmm.SyncIntervalMinutes"/> — fully runtime
/// editable; a config change picks up on the next cycle.
///
/// The master switch <see cref="SettingKeys.Trmm.Enabled"/> gates the
/// worker: when off the cycle is a no-op and only the next tick reads
/// settings, so flipping the switch on/off is cheap.
public sealed class TrmmSyncWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TrmmSyncWorker> _logger;

    public TrmmSyncWorker(IServiceProvider services, ILogger<TrmmSyncWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TrmmSyncWorker started.");

        // Stagger the first cycle a little so the boot-storm doesn't run
        // every integration's first sync at the same instant.
        await SafeDelayAsync(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunCycleAsync("scheduled", stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrmmSyncWorker cycle crashed — will retry.");
                delay = TimeSpan.FromMinutes(1);
            }

            await SafeDelayAsync(delay, stoppingToken);
        }

        _logger.LogInformation("TrmmSyncWorker stopped.");
    }

    private async Task<TimeSpan> RunCycleAsync(string trigger, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct);
        var intervalMinutes = Math.Clamp(
            await settings.GetAsync<int>(SettingKeys.Trmm.SyncIntervalMinutes, ct),
            1, 1440);

        if (!enabled)
        {
            return TimeSpan.FromMinutes(intervalMinutes);
        }

        var sync = scope.ServiceProvider.GetRequiredService<ITrmmSyncService>();
        var notifier = scope.ServiceProvider.GetRequiredService<ITrmmSyncNotifier>();

        var outcome = await sync.RunOnceAsync(trigger, ct);
        if (outcome.Success)
        {
            await notifier.NotifyAssetsChangedAsync(outcome, ct);
        }

        return TimeSpan.FromMinutes(intervalMinutes);
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
