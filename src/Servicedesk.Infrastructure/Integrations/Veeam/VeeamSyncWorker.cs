using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Veeam;

/// Periodically re-reads the Veeam backup status for the M365-connected
/// companies, on the Veeam.SyncIntervalMinutes cadence. Gated by Veeam.Enabled —
/// when off, the tick is a no-op so no Veeam API calls are made.
public sealed class VeeamSyncWorker : PeriodicWorker
{
    public VeeamSyncWorker(IServiceProvider sp, ILogger<VeeamSyncWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "veeam-sync";
    // Stagger past the M365 (120s) and Sophos (150s) workers so startup reads
    // don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(180);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(6);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = await settings.GetAsync<int>(SettingKeys.Veeam.SyncIntervalMinutes, ct);
        var enabled = await settings.GetAsync<bool>(SettingKeys.Veeam.Enabled, ct);
        return new WorkerSchedule(TimeSpan.FromMinutes(Math.Max(60, minutes)), enabled);
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        await services.GetRequiredService<IVeeamSyncService>().SyncAsync(ct);
    }
}
