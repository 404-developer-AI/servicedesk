using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Sla;

/// Periodically re-runs the SLA engine for every open ticket so business-minutes
/// consumed, paused-state, and breach flags stay fresh even when no ticket
/// event fires. Cadence is driven by Sla.RecalcIntervalSeconds.
public sealed class SlaRecalcWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SlaRecalcWorker> _logger;

    public SlaRecalcWorker(IServiceProvider sp, ILogger<SlaRecalcWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                interval = Math.Max(15, await settings.GetAsync<int>(SettingKeys.Sla.RecalcIntervalSeconds, stoppingToken));

                var repo = scope.ServiceProvider.GetRequiredService<ISlaRepository>();
                var engine = scope.ServiceProvider.GetRequiredService<ISlaEngine>();
                var ids = await repo.ListActiveTicketIdsAsync(500, stoppingToken);
                foreach (var id in ids)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await engine.RecalcAsync(id, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SLA recalc worker iteration failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
