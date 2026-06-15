using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.M365;

/// Periodic reader for every connected customer M365 tenant. Opt-in: a tick
/// does real work only when <c>M365.Enabled</c> is on. The cadence is
/// <c>M365.SyncIntervalMinutes</c> (floor 60). Each tick walks the active
/// tenant links, reads users + mailbox types + licenses app-only, and skips the
/// rewrite when a content hash shows nothing changed. A read denied with
/// 401/403 flips that company's link to needs_reconsent; the rest keep syncing.
public sealed class M365SyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<M365SyncWorker> _logger;

    private int _running;

    public M365SyncWorker(IServiceProvider sp, ILogger<M365SyncWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the Adsolut workers so startup reads don't all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 6 * 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.M365.SyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(60, minutes) * 60;

                var enabled = await settings.GetAsync<bool>(SettingKeys.M365.Enabled, stoppingToken);
                if (enabled)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                    {
                        _logger.LogWarning("M365 sync tick skipped: previous tick still running.");
                    }
                    else
                    {
                        try
                        {
                            var sync = scope.ServiceProvider.GetRequiredService<IM365SyncService>();
                            await sync.SyncAllAsync(stoppingToken);

                            var store = scope.ServiceProvider.GetRequiredService<IM365TenantStore>();
                            await store.PurgeExpiredConsentStatesAsync(DateTime.UtcNow, stoppingToken);
                        }
                        finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "M365 sync tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
