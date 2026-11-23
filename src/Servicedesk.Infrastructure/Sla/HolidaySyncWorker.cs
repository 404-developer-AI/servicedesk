using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Sla;

/// Daily job: for every schema with a non-empty country_code, pull this year
/// + next year of public holidays from Nager.Date and upsert them. Hours of
/// work between runs; a single transient failure is logged but does not halt
/// the loop.
public sealed class HolidaySyncWorker : PeriodicWorker
{
    public HolidaySyncWorker(IServiceProvider sp, ILogger<HolidaySyncWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "holiday-sync";
    // Delay initial run so the app has time to finish bootstrap/seeders.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(30);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(24);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var enabled = await settings.GetAsync<bool>(SettingKeys.Sla.HolidaysAutoSync, ct);
        return new WorkerSchedule(FallbackInterval, enabled);
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        var repo = services.GetRequiredService<ISlaRepository>();
        var sync = services.GetRequiredService<IHolidaySyncService>();

        var schemas = await repo.ListSchemasAsync(ct);
        var year = DateTime.UtcNow.Year;
        foreach (var s in schemas)
        {
            if (string.IsNullOrWhiteSpace(s.CountryCode)) continue;
            try
            {
                await sync.SyncAsync(s.Id, s.CountryCode, year, ct);
                await sync.SyncAsync(s.Id, s.CountryCode, year + 1, ct);
                run.AddItems(1);
            }
            catch (Exception ex)
            {
                run.Fail(ex);
                Logger.LogWarning(ex, "Holiday sync failed for schema {Schema} ({Country}).", s.Id, s.CountryCode);
            }
        }
    }
}
