using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Periodic TD SYNNEX order sync (Order Management). Opt-in: a tick does
/// real work only when <c>TdSynnex.Enabled</c> is on. Cadence
/// <c>TdSynnex.SyncIntervalMinutes</c> (floor 10); the active refresh inside
/// a pass follows <c>TdSynnex.ActiveRefreshMinutes</c>. The sync service
/// guards against overlapping runs.
public sealed class TdSynnexSyncWorker : PeriodicWorker
{
    public TdSynnexSyncWorker(IServiceProvider sp, ILogger<TdSynnexSyncWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "tdsynnex-sync";
    // Stagger past the other integration workers so startup reads spread out.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(180);
    protected override TimeSpan FallbackInterval => TimeSpan.FromMinutes(30);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = await settings.GetAsync<int>(SettingKeys.TdSynnex.SyncIntervalMinutes, ct);
        var enabled = await settings.GetAsync<bool>(SettingKeys.TdSynnex.Enabled, ct);
        return new WorkerSchedule(TimeSpan.FromMinutes(Math.Max(10, minutes)), enabled);
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        await services.GetRequiredService<ITdSynnexSyncService>().SyncAsync(forceActiveRefresh: false, ct);
    }
}
