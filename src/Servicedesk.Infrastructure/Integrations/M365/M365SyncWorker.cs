using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.M365;

/// Periodic reader for every connected customer M365 tenant. Opt-in: a tick
/// does real work only when <c>M365.Enabled</c> is on. The cadence is
/// <c>M365.SyncIntervalMinutes</c> (floor 60). Each tick walks the active
/// tenant links, reads users + mailbox types + licenses app-only, and skips the
/// rewrite when a content hash shows nothing changed. A read denied with
/// 401/403 flips that company's link to needs_reconsent; the rest keep syncing.
public sealed class M365SyncWorker : PeriodicWorker
{
    public M365SyncWorker(IServiceProvider sp, ILogger<M365SyncWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "m365-sync";
    // Stagger past the Adsolut workers so startup reads don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(120);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(6);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = await settings.GetAsync<int>(SettingKeys.M365.SyncIntervalMinutes, ct);
        var enabled = await settings.GetAsync<bool>(SettingKeys.M365.Enabled, ct);
        return new WorkerSchedule(TimeSpan.FromMinutes(Math.Max(60, minutes)), enabled);
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        await services.GetRequiredService<IM365SyncService>().SyncAllAsync(ct);
        await services.GetRequiredService<IM365TenantStore>().PurgeExpiredConsentStatesAsync(DateTime.UtcNow, ct);
    }
}
