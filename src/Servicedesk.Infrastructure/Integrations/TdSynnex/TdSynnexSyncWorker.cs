using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Periodic TD SYNNEX order sync (Order Management). Opt-in: a tick does
/// real work only when <c>TdSynnex.Enabled</c> is on. Cadence
/// <c>TdSynnex.SyncIntervalMinutes</c> (floor 10); the active refresh inside
/// a pass follows <c>TdSynnex.ActiveRefreshMinutes</c>. The sync service
/// guards against overlapping runs.
public sealed class TdSynnexSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<TdSynnexSyncWorker> _logger;

    public TdSynnexSyncWorker(IServiceProvider sp, ILogger<TdSynnexSyncWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the other integration workers so startup reads spread out.
        try { await Task.Delay(TimeSpan.FromSeconds(180), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 30 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.TdSynnex.SyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(10, minutes) * 60;

                if (await settings.GetAsync<bool>(SettingKeys.TdSynnex.Enabled, stoppingToken))
                {
                    using var run = PerfWorkerRun.Start("tdsynnex-sync");
                    try
                    {
                        var sync = scope.ServiceProvider.GetRequiredService<ITdSynnexSyncService>();
                        await sync.SyncAsync(forceActiveRefresh: false, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        run.Fail(ex);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TD SYNNEX sync tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
