using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Veeam;

/// Periodically re-reads the Veeam backup status for the M365-connected
/// companies, on the Veeam.SyncIntervalMinutes cadence. Gated by Veeam.Enabled —
/// when off, the tick is a no-op so no Veeam API calls are made.
public sealed class VeeamSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<VeeamSyncWorker> _logger;

    public VeeamSyncWorker(IServiceProvider sp, ILogger<VeeamSyncWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the M365 (120s) and Sophos (150s) workers so startup reads
        // don't all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(180), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 6 * 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Veeam.SyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(60, minutes) * 60;

                var enabled = await settings.GetAsync<bool>(SettingKeys.Veeam.Enabled, stoppingToken);
                if (enabled)
                {
                    var sync = scope.ServiceProvider.GetRequiredService<IVeeamSyncService>();
                    await sync.SyncAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Veeam sync tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
