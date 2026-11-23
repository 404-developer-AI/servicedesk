using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
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
public sealed class IntakeFormExpiryWorker : PeriodicWorker
{
    public IntakeFormExpiryWorker(IServiceProvider sp, ILogger<IntakeFormExpiryWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "intake-expiry";
    // Stagger start-up so every background worker isn't racing the
    // database at boot.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(20);
    protected override TimeSpan FallbackInterval => TimeSpan.FromMinutes(15);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = Math.Max(1, await settings.GetAsync<int>(SettingKeys.IntakeForms.ExpirySweepMinutes, ct));
        return new WorkerSchedule(TimeSpan.FromMinutes(minutes));
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IIntakeFormRepository>();
        var listNotifier = services.GetRequiredService<ITicketListNotifier>();

        var expired = await repo.ExpireStaleAsync(maxBatch: 200, nowUtc: DateTime.UtcNow, ct);
        run.AddItems(expired.Count);
        if (expired.Count == 0) return;

        Logger.LogInformation("Expired {Count} intake-form instance(s) this sweep.", expired.Count);
        foreach (var e in expired)
        {
            try { await listNotifier.NotifyUpdatedAsync(e.TicketId, ct); }
            catch (Exception notifyEx)
            {
                Logger.LogWarning(notifyEx,
                    "IntakeFormExpiryWorker: failed to broadcast TicketUpdated for {TicketId}.", e.TicketId);
            }
        }
    }
}
