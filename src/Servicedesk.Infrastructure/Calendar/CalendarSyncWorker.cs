using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;

namespace Servicedesk.Infrastructure.Calendar;

/// v0.1.61 — writes planned appointments to Outlook. Woken right after a
/// form save (CalendarSyncSignal), otherwise every minute for retries. Does
/// nothing while Calendar.Enabled is off.
public sealed class CalendarSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CalendarSyncSignal _signal;
    private readonly ILogger<CalendarSyncWorker> _logger;

    public CalendarSyncWorker(IServiceScopeFactory scopes, CalendarSyncSignal signal, ILogger<CalendarSyncWorker> logger)
    {
        _scopes = scopes;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            using (var run = PerfWorkerRun.Start("calendar-sync"))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ICalendarSyncService>();
                    processed = await svc.ProcessDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "CalendarSyncWorker tick failed.");
                }
            }

            // A full batch: go again right away; otherwise wait for a save or a minute.
            if (processed >= 20) continue;
            try { await _signal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
