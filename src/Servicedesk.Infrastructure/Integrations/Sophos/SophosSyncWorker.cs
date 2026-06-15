using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// Periodic Sophos Central spam-filter sync. Opt-in: a tick does real work only
/// when <c>Sophos.Enabled</c> is on. The cadence is
/// <c>Sophos.SyncIntervalMinutes</c> (floor 60). Each tick pulls the partner
/// tenant list, reconciles the local snapshot and refreshes the protected
/// mailbox set for every Microsoft 365-connected tenant. The sync service itself
/// guards against overlapping runs, so a long pass never stacks.
public sealed class SophosSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SophosSyncWorker> _logger;

    public SophosSyncWorker(IServiceProvider sp, ILogger<SophosSyncWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the M365 worker (120s) so startup reads don't all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(150), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 6 * 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Sophos.SyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(60, minutes) * 60;

                var enabled = await settings.GetAsync<bool>(SettingKeys.Sophos.Enabled, stoppingToken);
                if (enabled)
                {
                    var sync = scope.ServiceProvider.GetRequiredService<ISophosSyncService>();
                    await sync.SyncAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sophos sync tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
