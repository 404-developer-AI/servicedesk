using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// BackgroundService for the Remote Desktop check sync (v0.1.10). Own
/// cadence (<see cref="SettingKeys.Trmm.RdsSyncIntervalMinutes"/>, default
/// 60) because a cycle is one TRMM call per server. Gated on both the
/// integration master switch and <see cref="SettingKeys.Trmm.RdsCheckEnabled"/>;
/// settings are re-read every tick so a change picks up without redeploy.
public sealed class TrmmRdsSyncWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TrmmRdsSyncWorker> _logger;

    public TrmmRdsSyncWorker(IServiceProvider services, ILogger<TrmmRdsSyncWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TrmmRdsSyncWorker started.");

        // Start well after the agent mirror's first cycle (20 s) so the
        // first RDS pass already sees the current server list.
        await SafeDelayAsync(TimeSpan.FromSeconds(90), stoppingToken);

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
                _logger.LogError(ex, "TrmmRdsSyncWorker cycle crashed — will retry.");
                delay = TimeSpan.FromMinutes(5);
            }

            await SafeDelayAsync(delay, stoppingToken);
        }

        _logger.LogInformation("TrmmRdsSyncWorker stopped.");
    }

    private async Task<TimeSpan> RunCycleAsync(string trigger, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct)
                      && await settings.GetAsync<bool>(SettingKeys.Trmm.RdsCheckEnabled, ct);
        var intervalMinutes = Math.Clamp(
            await settings.GetAsync<int>(SettingKeys.Trmm.RdsSyncIntervalMinutes, ct),
            5, 1440);

        if (!enabled)
        {
            // Dormant: poll the switch at a fixed short cadence so
            // enabling it in Settings takes effect within minutes, not
            // after a full (possibly day-long) interval.
            return TimeSpan.FromMinutes(Math.Min(intervalMinutes, 5));
        }

        var sync = scope.ServiceProvider.GetRequiredService<ITrmmRdsSyncService>();
        var notifier = scope.ServiceProvider.GetRequiredService<ITrmmSyncNotifier>();

        var outcome = await sync.RunOnceAsync(trigger, ct);
        if (outcome.Success)
        {
            await notifier.NotifyRemoteDesktopChangedAsync(new
            {
                kind = "rds-sync",
                agents = outcome.Agents,
                rds = outcome.Rds,
                attention = outcome.Failed + outcome.NoCheck + outcome.Pending + outcome.Errors,
            }, ct);
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
