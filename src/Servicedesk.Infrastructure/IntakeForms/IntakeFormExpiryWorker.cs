using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.IntakeForms;

/// Periodically flips Sent intake-form instances past their <c>expires_utc</c>
/// into the Expired state and writes an <c>IntakeFormExpired</c> ticket event
/// per touched row. Cadence driven by <c>IntakeForms.ExpirySweepMinutes</c>.
///
/// The public endpoint already re-checks expiry on GET + submit so a link
/// cannot succeed between sweeps. This worker exists so the agent-side
/// timeline reflects expiry without waiting for someone to open the link,
/// and so reporting queries over <c>status</c> are eventually consistent.
public sealed class IntakeFormExpiryWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<IntakeFormExpiryWorker> _logger;

    public IntakeFormExpiryWorker(IServiceProvider sp, ILogger<IntakeFormExpiryWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger start-up so every background worker isn't racing the
        // database at boot.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = 15;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                intervalMinutes = Math.Max(1, await settings.GetAsync<int>(SettingKeys.IntakeForms.ExpirySweepMinutes, stoppingToken));

                var repo = scope.ServiceProvider.GetRequiredService<IIntakeFormRepository>();
                var listNotifier = scope.ServiceProvider.GetRequiredService<ITicketListNotifier>();

                var expired = await repo.ExpireStaleAsync(maxBatch: 200, nowUtc: DateTime.UtcNow, stoppingToken);
                if (expired.Count > 0)
                {
                    _logger.LogInformation("Expired {Count} intake-form instance(s) this sweep.", expired.Count);
                    foreach (var e in expired)
                    {
                        try
                        {
                            await listNotifier.NotifyUpdatedAsync(e.TicketId, stoppingToken);
                        }
                        catch (Exception notifyEx)
                        {
                            _logger.LogWarning(notifyEx,
                                "IntakeFormExpiryWorker: failed to broadcast TicketUpdated for {TicketId}.", e.TicketId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IntakeFormExpiryWorker iteration failed.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
