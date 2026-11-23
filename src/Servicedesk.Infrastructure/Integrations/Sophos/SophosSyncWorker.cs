using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// Periodic Sophos Central spam-filter sync. Opt-in: a tick does real work only
/// when <c>Sophos.Enabled</c> is on. The cadence is
/// <c>Sophos.SyncIntervalMinutes</c> (floor 60). Each tick pulls the partner
/// tenant list, reconciles the local snapshot and refreshes the protected
/// mailbox set for every Microsoft 365-connected tenant. The sync service itself
/// guards against overlapping runs, so a long pass never stacks.
public sealed class SophosSyncWorker : PeriodicWorker
{
    public SophosSyncWorker(IServiceProvider sp, ILogger<SophosSyncWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "sophos-sync";
    // Stagger past the M365 worker (120s) so startup reads don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(150);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(6);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = await settings.GetAsync<int>(SettingKeys.Sophos.SyncIntervalMinutes, ct);
        var enabled = await settings.GetAsync<bool>(SettingKeys.Sophos.Enabled, ct);
        return new WorkerSchedule(TimeSpan.FromMinutes(Math.Max(60, minutes)), enabled);
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        await services.GetRequiredService<ISophosSyncService>().SyncAsync(ct);
    }
}
