using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
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
public sealed class SurveyExpiryWorker : PeriodicWorker
{
    public SurveyExpiryWorker(IServiceProvider sp, ILogger<SurveyExpiryWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "survey-expiry";
    // Stagger start so the survey + intake workers don't both hammer
    // the DB the second the host comes up.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(30);
    protected override TimeSpan FallbackInterval => TimeSpan.FromMinutes(15);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = Math.Max(1, await settings.GetAsync<int>(SettingKeys.Surveys.ExpirySweepMinutes, ct));
        return new WorkerSchedule(TimeSpan.FromMinutes(minutes));
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        var repo = services.GetRequiredService<ISurveyInvitationRepository>();
        var listNotifier = services.GetRequiredService<ITicketListNotifier>();

        var expired = await repo.ExpireStaleAsync(maxBatch: 200, nowUtc: DateTime.UtcNow, ct);
        run.AddItems(expired.Count);
        if (expired.Count == 0) return;

        Logger.LogInformation("Expired {Count} survey invitation(s) this sweep.", expired.Count);
        foreach (var e in expired)
        {
            try { await listNotifier.NotifyUpdatedAsync(e.TicketId, ct); }
            catch (Exception notifyEx)
            {
                Logger.LogWarning(notifyEx,
                    "SurveyExpiryWorker: failed to broadcast TicketUpdated for {TicketId}.", e.TicketId);
            }
        }
    }
}
