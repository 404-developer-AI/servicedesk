using System.Globalization;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

public interface IPerfSettings
{
    PerfOptions Options { get; }

    /// The level in force right now (Diagnose expires on server time).
    PerfLevel Level { get; }

    bool IsEnabled(PerfCollector collector);

    /// Collector on AND Diagnose active.
    bool IsDiagnose(PerfCollector collector);

    TimeProvider Time { get; }

    /// Re-reads the settings store (itself an in-memory cache).
    Task RefreshAsync(CancellationToken ct);
}

/// Holds the performance settings as one immutable snapshot so the hot paths
/// pay a single volatile read. Refreshed every few seconds by
/// <see cref="PerfFlushService"/> and immediately after a write through the
/// Performance endpoints, so a toggle takes effect without a restart.
public sealed class PerfSettingsProvider : IPerfSettings
{
    private readonly ISettingsService _settings;
    private readonly ILogger<PerfSettingsProvider> _logger;
    private PerfOptions _options = PerfOptions.Defaults;

    public PerfSettingsProvider(ISettingsService settings, TimeProvider time, ILogger<PerfSettingsProvider> logger)
    {
        _settings = settings;
        Time = time;
        _logger = logger;
        PerfRuntime.Settings = this;
    }

    public TimeProvider Time { get; }

    public PerfOptions Options => Volatile.Read(ref _options);

    public PerfLevel Level => Options.EffectiveLevel(Time.GetUtcNow());

    public bool IsEnabled(PerfCollector collector)
    {
        var options = Options;
        return options.BaseLevel != PerfLevel.Off && (options.Collectors & collector) == collector;
    }

    public bool IsDiagnose(PerfCollector collector) => IsEnabled(collector) && Level == PerfLevel.Diagnose;

    /// Test hook: install a snapshot directly.
    internal void Set(PerfOptions options) => Volatile.Write(ref _options, options);

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            Volatile.Write(ref _options, await LoadAsync(_settings, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the previous snapshot — a settings hiccup must never
            // flip monitoring on/off by itself.
            _logger.LogDebug(ex, "Performance settings refresh failed; keeping the previous snapshot.");
        }
    }

    public static async Task<PerfOptions> LoadAsync(ISettingsService s, CancellationToken ct)
    {
        var level = ParseLevel(await s.GetAsync<string>(SettingKeys.Performance.Level, ct));
        var until = ParseUtc(await s.GetAsync<string>(SettingKeys.Performance.DiagnoseUntilUtc, ct));

        var collectors = PerfCollector.None;
        async Task Flag(string key, PerfCollector c)
        {
            if (await s.GetAsync<bool>(key, ct)) collectors |= c;
        }
        await Flag(SettingKeys.Performance.CollectorHttp, PerfCollector.Http);
        await Flag(SettingKeys.Performance.CollectorDatabase, PerfCollector.Database);
        await Flag(SettingKeys.Performance.CollectorPostgres, PerfCollector.Postgres);
        await Flag(SettingKeys.Performance.CollectorRuntime, PerfCollector.Runtime);
        await Flag(SettingKeys.Performance.CollectorHost, PerfCollector.Host);
        await Flag(SettingKeys.Performance.CollectorFrontend, PerfCollector.Frontend);
        await Flag(SettingKeys.Performance.CollectorSignalR, PerfCollector.SignalR);
        await Flag(SettingKeys.Performance.CollectorWorkers, PerfCollector.Workers);
        await Flag(SettingKeys.Performance.CollectorExternal, PerfCollector.External);

        return new PerfOptions(
            level,
            until,
            collectors,
            Math.Clamp(await s.GetAsync<int>(SettingKeys.Performance.RumSamplePercent, ct), 0, 100),
            Math.Clamp(await s.GetAsync<int>(SettingKeys.Performance.SlowRequestThresholdMs, ct), 50, 60_000),
            Math.Clamp(await s.GetAsync<int>(SettingKeys.Performance.SlowQueryThresholdMs, ct), 1, 60_000),
            Math.Clamp(await s.GetAsync<int>(SettingKeys.Performance.NPlusOneThreshold, ct), 2, 1000),
            await s.GetAsync<bool>(SettingKeys.Performance.ServerTimingEnabled, ct),
            Math.Clamp(await s.GetAsync<int>(SettingKeys.Performance.PgSnapshotIntervalMinutes, ct), 1, 60),
            Math.Clamp(await s.GetAsync<int>(SettingKeys.Performance.PgSnapshotDiagnoseIntervalMinutes, ct), 1, 60));
    }

    public static PerfLevel ParseLevel(string? raw) =>
        string.Equals(raw?.Trim(), "off", StringComparison.OrdinalIgnoreCase) ? PerfLevel.Off : PerfLevel.Basic;

    public static DateTimeOffset? ParseUtc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v) ? v : null;
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
