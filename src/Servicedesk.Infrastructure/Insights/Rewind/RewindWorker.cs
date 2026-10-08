using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Insights.Rewind;

/// Fires one Rewind capture on every interval boundary of server time
/// (default every quarter hour: :00, :15, :30, :45). Only boundaries are
/// captured — after a restart mid-quarter the worker waits for the next
/// one instead of labelling a 12:52 state as 12:45, so a gap stays a gap.
public sealed class RewindWorker : BackgroundService
{
    /// Captures run this long after the boundary, so a mutation committed
    /// on the boundary itself is in.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly IRewindCaptureService _capture;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<RewindWorker> _logger;

    public RewindWorker(
        IRewindCaptureService capture, ISettingsService settings, TimeProvider time, ILogger<RewindWorker> logger)
    {
        _capture = capture;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int interval;
            try { interval = RewindSlots.NormalizeInterval(await _settings.GetAsync<int>(SettingKeys.Insights.RewindIntervalMinutes, stoppingToken)); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { interval = RewindSlots.DefaultIntervalMinutes; }

            var now = _time.GetUtcNow().UtcDateTime;
            var slot = RewindSlots.Floor(now, interval).AddMinutes(interval);
            var wait = slot + Settle - now;
            try { if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, stoppingToken); }
            catch (OperationCanceledException) { return; }

            // Too late for this slot (machine slept, long GC) → skip it.
            if (_time.GetUtcNow().UtcDateTime - slot >= TimeSpan.FromMinutes(interval)) continue;

            using var run = PerfWorkerRun.Start("rewind");
            try
            {
                var changed = await _capture.CaptureAsync(slot, interval, stoppingToken);
                run.AddItems(changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                run.Fail(ex);
                _logger.LogWarning(ex, "Rewind capture for slot {Slot:O} failed.", slot);
            }
        }
    }
}

/// Slot arithmetic shared by the worker and the read side. Slots are
/// aligned on UTC multiples of the interval, which coincides with local
/// quarter hours in every real time zone.
public static class RewindSlots
{
    public const int DefaultIntervalMinutes = 15;
    public static readonly int[] AllowedIntervals = { 5, 10, 15, 30, 60 };

    public static int NormalizeInterval(int minutes)
        => Array.IndexOf(AllowedIntervals, minutes) >= 0 ? minutes : DefaultIntervalMinutes;

    public static DateTime Floor(DateTime utc, int intervalMinutes)
    {
        var ticks = TimeSpan.FromMinutes(intervalMinutes).Ticks;
        return new DateTime(utc.Ticks - (utc.Ticks % ticks), DateTimeKind.Utc);
    }
}
