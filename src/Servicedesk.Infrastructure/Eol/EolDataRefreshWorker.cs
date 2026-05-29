using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Eol;

/// BackgroundService that refreshes the local <c>eol_releases</c> cache
/// from endoflife.date on the cadence configured in
/// <see cref="SettingKeys.Eol.RefreshIntervalDays"/>. Master switch
/// <see cref="SettingKeys.Eol.Enabled"/> gates the cycle; the worker
/// reads both per tick so a settings toggle takes effect on the next
/// run without redeploy.
public sealed class EolDataRefreshWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EolDataRefreshWorker> _logger;

    public EolDataRefreshWorker(IServiceProvider services, ILogger<EolDataRefreshWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EolDataRefreshWorker started.");

        // First-run delay so a fresh container boot doesn't immediately
        // race the rest of the integration sync workers.
        await SafeDelayAsync(TimeSpan.FromSeconds(45), stoppingToken);

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
                _logger.LogError(ex, "EolDataRefreshWorker cycle crashed — will retry.");
                delay = TimeSpan.FromHours(1);
            }
            await SafeDelayAsync(delay, stoppingToken);
        }

        _logger.LogInformation("EolDataRefreshWorker stopped.");
    }

    private async Task<TimeSpan> RunCycleAsync(string trigger, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var enabled = await settings.GetAsync<bool>(SettingKeys.Eol.Enabled, ct);
        var intervalDays = Math.Clamp(
            await settings.GetAsync<int>(SettingKeys.Eol.RefreshIntervalDays, ct),
            1, 90);
        var nextDelay = TimeSpan.FromDays(intervalDays);

        if (!enabled) return nextDelay;

        var svc = scope.ServiceProvider.GetRequiredService<IEolDataRefreshService>();
        await svc.RunOnceAsync(trigger, ct);
        return nextDelay;
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
