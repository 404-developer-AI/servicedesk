using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Sla;

/// Daily job: for every schema with a non-empty country_code, pull this year
/// + next year of public holidays from Nager.Date and upsert them. Hours of
/// work between runs; a single transient failure is logged but does not halt
/// the loop.
public sealed class HolidaySyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<HolidaySyncWorker> _logger;

    public HolidaySyncWorker(IServiceProvider sp, ILogger<HolidaySyncWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial run so the app has time to finish bootstrap/seeders.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var repo = scope.ServiceProvider.GetRequiredService<ISlaRepository>();
                var sync = scope.ServiceProvider.GetRequiredService<IHolidaySyncService>();

                var enabled = await settings.GetAsync<bool>(SettingKeys.Sla.HolidaysAutoSync, stoppingToken);
                if (enabled)
                {
                    var schemas = await repo.ListSchemasAsync(stoppingToken);
                    var year = DateTime.UtcNow.Year;
                    foreach (var s in schemas)
                    {
                        if (string.IsNullOrWhiteSpace(s.CountryCode)) continue;
                        try
                        {
                            await sync.SyncAsync(s.Id, s.CountryCode, year, stoppingToken);
                            await sync.SyncAsync(s.Id, s.CountryCode, year + 1, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Holiday sync failed for schema {Schema} ({Country}).", s.Id, s.CountryCode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Holiday sync worker loop error.");
            }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
