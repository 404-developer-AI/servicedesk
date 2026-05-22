using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Activity;

/// Daily prune of <c>agent_activity_events</c> older than
/// <see cref="SettingKeys.ActivityFeed.RetentionDays"/>. Deletes are
/// batched so a one-time backlog does not lock the table.
public sealed class ActivityRetentionWorker : BackgroundService
{
    private const int BatchSize = 5_000;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly ILogger<ActivityRetentionWorker> _logger;

    public ActivityRetentionWorker(
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        ILogger<ActivityRetentionWorker> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the rest of the host a moment to come up before the first
        // sweep — avoids contending with bootstrap on cold-start.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ActivityRetentionWorker tick failed.");
            }

            var intervalHours = await ReadIntervalAsync(stoppingToken);
            try
            {
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PruneAsync(CancellationToken stoppingToken)
    {
        var retentionDays = await ReadRetentionAsync(stoppingToken);
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retentionDays);

        const string sql = """
            DELETE FROM agent_activity_events
            WHERE id IN (
                SELECT id FROM agent_activity_events
                WHERE occurred_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(stoppingToken);
        long totalDeleted = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var rows = await connection.ExecuteAsync(new CommandDefinition(
                sql, new { Cutoff = cutoff, BatchSize }, cancellationToken: stoppingToken));
            totalDeleted += rows;
            if (rows < BatchSize) break;
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation(
                "ActivityRetentionWorker pruned {Count} rows older than {Cutoff:O} ({Days}d retention).",
                totalDeleted, cutoff, retentionDays);
        }
    }

    private async Task<int> ReadRetentionAsync(CancellationToken ct)
    {
        var raw = await _settings.GetAsync<int>(SettingKeys.ActivityFeed.RetentionDays, ct);
        return Math.Clamp(raw, 7, 3650);
    }

    private async Task<int> ReadIntervalAsync(CancellationToken ct)
    {
        var raw = await _settings.GetAsync<int>(SettingKeys.ActivityFeed.PruneIntervalHours, ct);
        return Math.Clamp(raw, 1, 168);
    }
}
