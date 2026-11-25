using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;

namespace Servicedesk.Infrastructure.Maintenance;

/// v0.1.67 (phase 3) — starts maintenance rounds whose mail moment (mail
/// day at the plan's mail time) has come (tickets + the round's campaign,
/// sent when the plan auto-sends). Runs every 15 minutes, so a round starts
/// up to 15 minutes after its mail time (stated in the UI); a full batch runs
/// again right away. Does nothing while Campaigns.MaintenanceEnabled is off.
public sealed class MaintenancePlanWorker : BackgroundService
{
    // Owner decision: a 15-minute tick is fine (low load); the UI states that a
    // round starts up to 15 minutes after the plan's mail time.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MaintenancePlanWorker> _logger;

    public MaintenancePlanWorker(IServiceScopeFactory scopes, ILogger<MaintenancePlanWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            using (var run = PerfWorkerRun.Start("maintenance-planner"))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    handled = await scope.ServiceProvider.GetRequiredService<IMaintenanceRoundService>().RunAsync(stoppingToken);
                    handled += await scope.ServiceProvider.GetRequiredService<IMaintenanceReminderService>().RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "MaintenancePlanWorker iteration failed.");
                }
            }

            if (handled >= 10) continue;
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
