using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Surveys;

/// Periodically flips Sent survey invitations past their <c>expires_utc</c>
/// into the Expired state and writes a <c>SurveyExpired</c> ticket event per
/// touched row. Cadence driven by <c>Surveys.ExpirySweepMinutes</c>.
///
/// The public endpoint also re-checks expiry on GET + submit so a link
/// cannot succeed between sweeps. This worker exists so the agent-side
/// timeline reflects expiry without waiting for someone to open the link.
public sealed class SurveyExpiryWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SurveyExpiryWorker> _logger;

    public SurveyExpiryWorker(IServiceProvider sp, ILogger<SurveyExpiryWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger start so the survey + intake workers don't both hammer
        // the DB the second the host comes up.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = 15;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                intervalMinutes = Math.Max(1, await settings.GetAsync<int>(SettingKeys.Surveys.ExpirySweepMinutes, stoppingToken));

                var repo = scope.ServiceProvider.GetRequiredService<ISurveyInvitationRepository>();
                var listNotifier = scope.ServiceProvider.GetRequiredService<ITicketListNotifier>();

                var expired = await repo.ExpireStaleAsync(maxBatch: 200, nowUtc: DateTime.UtcNow, stoppingToken);
                if (expired.Count > 0)
                {
                    _logger.LogInformation("Expired {Count} survey invitation(s) this sweep.", expired.Count);
                    foreach (var e in expired)
                    {
                        try { await listNotifier.NotifyUpdatedAsync(e.TicketId, stoppingToken); }
                        catch (Exception notifyEx)
                        {
                            _logger.LogWarning(notifyEx,
                                "SurveyExpiryWorker: failed to broadcast TicketUpdated for {TicketId}.", e.TicketId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SurveyExpiryWorker iteration failed.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
