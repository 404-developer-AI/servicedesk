using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Storage;

public enum BlobDiskLevel { Unknown, Ok, Warning, Critical }

public sealed record BlobDiskSample(
    DateTime SampledUtc, string RootPath, long TotalBytes, long FreeBytes, double UsedPercent,
    BlobDiskLevel Level, int WarnPercent, int CriticalPercent);

/// Latest disk sample of the blob-storage disk. Read by the Health page and
/// by the mail poller (which pauses while the level is Critical) — always
/// from memory, never a DB or filesystem call per read.
public interface IBlobDiskStatus
{
    BlobDiskSample? Latest { get; }
    bool IsCritical { get; }
}

/// Samples the blob-storage disk every <see cref="SampleInterval"/>: keeps
/// the latest sample in memory, writes one <c>blob_disk_samples</c> row per
/// sample (pruned by RetentionWorker, Retention.BlobDiskSamplesDays) and logs
/// level transitions — a Warning/Error log becomes a Health incident, which
/// is what alerts the admins. Database and blobs share one disk on a default
/// install, so Critical also protects PostgreSQL from a full disk.
public sealed class BlobDiskMonitor : BackgroundService, IBlobDiskStatus
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IBlobStoreMaintenance _store;
    private readonly ISettingsService _settings;
    private readonly ILogger<BlobDiskMonitor> _logger;
    private volatile BlobDiskSample? _latest;

    public BlobDiskMonitor(
        NpgsqlDataSource dataSource,
        IBlobStoreMaintenance store,
        ISettingsService settings,
        ILogger<BlobDiskMonitor> logger)
    {
        _dataSource = dataSource;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    public BlobDiskSample? Latest => _latest;
    public bool IsCritical => _latest?.Level == BlobDiskLevel.Critical;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var run = PerfWorkerRun.Start("blob-disk-monitor"))
            {
                try
                {
                    await SampleAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "Blob disk sample failed.");
                }
            }

            try { await Task.Delay(SampleInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task SampleAsync(CancellationToken ct)
    {
        var root = await _store.GetRootPathAsync(ct);
        if (!Directory.Exists(root)) return; // BlobStoreHealth reports the misconfiguration

        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.TotalSize <= 0) return;
        var total = drive.TotalSize;
        var free = drive.AvailableFreeSpace;
        var usedPercent = Math.Round(100.0 * (total - free) / total, 2);

        var warn = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Storage.BlobDiskWarnPercent, ct), 1, 100);
        var critical = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Storage.BlobDiskCriticalPercent, ct), 1, 100);
        var level = Classify(usedPercent, warn, critical);

        var previous = _latest?.Level ?? BlobDiskLevel.Unknown;
        _latest = new BlobDiskSample(DateTime.UtcNow, root, total, free, usedPercent, level, warn, critical);

        if (level != previous)
        {
            switch (level)
            {
                case BlobDiskLevel.Critical:
                    _logger.LogError(
                        "Blob storage disk is {Used:0.0}% full (critical at {Critical}%). Mail polling is paused until usage drops.",
                        usedPercent, critical);
                    break;
                case BlobDiskLevel.Warning:
                    _logger.LogWarning(
                        "Blob storage disk is {Used:0.0}% full (warning at {Warn}%).", usedPercent, warn);
                    break;
                case BlobDiskLevel.Ok when previous is BlobDiskLevel.Warning or BlobDiskLevel.Critical:
                    _logger.LogInformation(
                        "Blob storage disk back to {Used:0.0}% — below the warning threshold; mail polling runs normally.", usedPercent);
                    break;
            }
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO blob_disk_samples (root_path, total_bytes, free_bytes, used_percent)
            VALUES (@Root, @Total, @Free, @Used)
            """, new { Root = root, Total = total, Free = free, Used = (decimal)usedPercent }, cancellationToken: ct));
    }

    internal static BlobDiskLevel Classify(double usedPercent, int warnPercent, int criticalPercent) =>
        usedPercent >= criticalPercent ? BlobDiskLevel.Critical
        : usedPercent >= warnPercent ? BlobDiskLevel.Warning
        : BlobDiskLevel.Ok;
}
