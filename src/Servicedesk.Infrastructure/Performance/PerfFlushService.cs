using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

/// The monitor's heartbeat:
/// <list type="bullet">
/// <item>every 5 s — refresh the in-memory settings snapshot (so a toggle or
///   a Diagnose expiry applies within seconds, without a restart);</item>
/// <item>every 10 s — sample the .NET runtime and the host;</item>
/// <item>every minute boundary — swap the measurement window and write the
///   retired one to Postgres in one batch.</item>
/// </list>
/// A failure here is logged (rate-limited) and the data of that minute is
/// dropped; it never reaches a request.
public sealed class PerfFlushService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private const int SampleEveryTicks = 2;

    private readonly PerfRecorder _recorder;
    private readonly IPerfSettings _settings;
    private readonly PerfInstrumentation _instrumentation;
    private readonly PerfWriter _writer;
    private readonly ISettingsService _settingsStore;
    private readonly ILogger<PerfFlushService> _logger;
    private readonly RuntimeSampler _runtime = new();
    private readonly HostMetricsReader _host = new();
    private DateTimeOffset _lastErrorLog = DateTimeOffset.MinValue;
    private string? _blobRoot;

    public PerfFlushService(
        PerfRecorder recorder,
        IPerfSettings settings,
        PerfInstrumentation instrumentation,
        PerfWriter writer,
        ISettingsService settingsStore,
        ILogger<PerfFlushService> logger)
    {
        _recorder = recorder;
        _settings = settings;
        _instrumentation = instrumentation;
        _writer = writer;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the bootstrapper create the schema and seed the settings first.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        PerfContext.Worker = "perf-flush";
        await _settings.RefreshAsync(stoppingToken);
        _recorder.Swap(_settings.Time.GetUtcNow()); // discard the warm-up partial window
        var tick = 0;
        var currentMinute = MinuteOf(_settings.Time.GetUtcNow());

        using var timer = new PeriodicTimer(Tick, _settings.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tick++;
                await _settings.RefreshAsync(stoppingToken);

                if (tick % SampleEveryTicks == 0)
                {
                    _blobRoot ??= await TryBlobRootAsync(stoppingToken);
                    Sample();
                }

                var nowMinute = MinuteOf(_settings.Time.GetUtcNow());
                if (nowMinute > currentMinute)
                {
                    await FlushAsync(currentMinute, stoppingToken);
                    currentMinute = nowMinute;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down — StopAsync flushes the last partial minute
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Persist what the current (partial) minute collected so an update
        // or restart does not leave a hole in the charts.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await FlushAsync(MinuteOf(_settings.Time.GetUtcNow()), cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Final performance flush on shutdown skipped.");
        }
    }

    private void Sample()
    {
        try
        {
            if (_settings.Level == PerfLevel.Off || !_settings.IsEnabled(PerfCollector.Runtime)) return;
            _instrumentation.PollObservables();
            HostSample? host = null;
            if (_settings.IsEnabled(PerfCollector.Host) && HostMetricsReader.IsSupported)
            {
                host = _host.Read(_blobRoot);
            }
            var sample = _runtime.Sample(_settings.Time.GetUtcNow(), _instrumentation.Gauges, _recorder.InFlight, host);
            _recorder.Current.AddRuntimeSample(sample);
        }
        catch (Exception ex)
        {
            LogRateLimited(ex, "Performance runtime sample failed.");
        }
    }

    private async Task FlushAsync(DateTimeOffset bucket, CancellationToken ct)
    {
        var retired = _recorder.Swap(_settings.Time.GetUtcNow());
        var level = _settings.Level;
        if (level == PerfLevel.Off && IsEmpty(retired)) return;

        // Collectors that grabbed the old window just before the swap may
        // still be adding to it — give them a moment before reading.
        try { await Task.Delay(250, ct); } catch (OperationCanceledException) { }

        try
        {
            await _writer.WriteAsync(retired, bucket, level.ToString().ToLowerInvariant(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogRateLimited(ex, "Performance flush failed; this minute's metrics were dropped.");
        }
    }

    private static bool IsEmpty(PerfWindow w) =>
        w.Http.IsEmpty && w.Db.IsEmpty && w.Spans.IsEmpty && w.Rum.IsEmpty && w.SlowRequests.IsEmpty &&
        w.WorkerRuns.IsEmpty && w.RuntimeSamples.Count == 0;

    private async Task<string?> TryBlobRootAsync(CancellationToken ct)
    {
        try
        {
            return await _settingsStore.GetAsync<string>(SettingKeys.Storage.BlobRoot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private void LogRateLimited(Exception ex, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastErrorLog < TimeSpan.FromMinutes(10)) return;
        _lastErrorLog = now;
        _logger.LogWarning(ex, message);
    }

    public static DateTimeOffset MinuteOf(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
}
