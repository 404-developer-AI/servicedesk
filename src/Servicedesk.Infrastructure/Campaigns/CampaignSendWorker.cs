using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — drains the campaign outbox one mail at a time, paced by
/// Campaigns.SendRatePerMinute so a large campaign never crowds out normal
/// ticket mail on the shared mailbox (Exchange Online allows ~30/min per
/// mailbox). Woken right after a round is queued (<see cref="CampaignSendSignal"/>),
/// otherwise every minute for retries and housekeeping. Each iteration runs
/// in its own scope inside <see cref="PerfWorkerRun"/>.
public sealed class CampaignSendWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CampaignSendSignal _signal;
    private readonly ILogger<CampaignSendWorker> _logger;

    public CampaignSendWorker(IServiceScopeFactory scopes, CampaignSendSignal signal, ILogger<CampaignSendWorker> logger)
    {
        _scopes = scopes;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var lastHousekeeping = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = false;
            var pause = TimeSpan.FromSeconds(3);
            using (var run = PerfWorkerRun.Start("campaign-send"))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ICampaignSendService>();
                    var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                    if (DateTime.UtcNow - lastHousekeeping > TimeSpan.FromSeconds(30))
                    {
                        await svc.HousekeepAsync(stoppingToken);
                        lastHousekeeping = DateTime.UtcNow;
                    }

                    sent = await svc.SendNextAsync(stoppingToken);
                    if (sent)
                    {
                        var rate = Math.Clamp(await settings.GetOrAsync<int>(SettingKeys.Campaigns.SendRatePerMinute, 20, stoppingToken), 1, 60);
                        pause = TimeSpan.FromSeconds(60.0 / rate);
                        // A finished round is reported promptly.
                        lastHousekeeping = DateTime.MinValue;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "CampaignSendWorker iteration failed.");
                }
            }

            try
            {
                if (sent) await Task.Delay(pause, stoppingToken);
                else await _signal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
