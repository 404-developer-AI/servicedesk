using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Retention;

/// v0.0.101 — one generic sweep for the housekeeping tables that grew
/// without bound: expired/revoked sessions, old notifications, finished
/// attachment jobs (+ their attempt history via FK cascade), acknowledged
/// incidents and disk samples. Modelled on ActivityRetentionWorker: batched
/// deletes ordered by primary key so a first-time backlog never holds a long
/// lock, per-table retention in days (0 = keep forever), one interval.
///
/// Deliberately NOT covered: audit_log (hash-chained, tamper-evident, kept
/// unbounded by design — archival is a separate decision), survey
/// invitations (they carry the survey results), M365 report sends (send
/// log, low volume), Zammad import records (one-off import artefact).
public sealed class RetentionWorker : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IRetentionHealth _health;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        IRetentionHealth health,
        ILogger<RetentionWorker> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _health = health;
        _logger = logger;
    }

    /// One prunable table: label for logs/health, the batched DELETE (must
    /// select by primary key with LIMIT @BatchSize and filter on @Cutoff),
    /// and the setting that holds its retention in days.
    private sealed record Rule(string Table, string SettingKey, string Sql);

    private static readonly Rule[] Rules =
    {
        // Sessions that can never authenticate again: revoked, or past their
        // hard expiry. Cutoff applies to the moment they became dead so an
        // admin can still see "recently expired" sessions on the user page.
        new("user_sessions", SettingKeys.Retention.UserSessionsDays, """
            DELETE FROM user_sessions
            WHERE id IN (
                SELECT id FROM user_sessions
                WHERE (revoked_utc IS NOT NULL OR expires_utc < now())
                  AND COALESCE(revoked_utc, expires_utc) < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Notifications the user has already seen or acknowledged.
        new("user_notifications (read)", SettingKeys.Retention.NotificationsReadDays, """
            DELETE FROM user_notifications
            WHERE id IN (
                SELECT id FROM user_notifications
                WHERE (viewed_utc IS NOT NULL OR acked_utc IS NOT NULL)
                  AND created_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Unread notifications get a (much longer) separate window — a badge
        // nobody clicked for a year is noise, not a to-do.
        new("user_notifications (unread)", SettingKeys.Retention.NotificationsUnreadDays, """
            DELETE FROM user_notifications
            WHERE id IN (
                SELECT id FROM user_notifications
                WHERE viewed_utc IS NULL AND acked_utc IS NULL
                  AND created_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Finished attachment jobs. DeadLettered rows are excluded on purpose:
        // they sit on the Health page until an admin requeues or cancels
        // them, and silently vanishing would clear a Critical. attempts go
        // with the job (ON DELETE CASCADE).
        new("attachment_jobs", SettingKeys.Retention.AttachmentJobsDays, """
            DELETE FROM attachment_jobs
            WHERE id IN (
                SELECT id FROM attachment_jobs
                WHERE state IN ('Succeeded','Failed','Cancelled')
                  AND updated_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Acknowledged incidents (the Health archive). Open ones are never touched.
        new("incidents", SettingKeys.Retention.IncidentsDays, """
            DELETE FROM incidents
            WHERE id IN (
                SELECT id FROM incidents
                WHERE acknowledged_utc IS NOT NULL AND acknowledged_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        new("blob_disk_samples", SettingKeys.Retention.BlobDiskSamplesDays, """
            DELETE FROM blob_disk_samples
            WHERE id IN (
                SELECT id FROM blob_disk_samples
                WHERE sampled_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let bootstrap + the other workers settle first.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = 6;
            try
            {
                intervalHours = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Retention.RunIntervalHours, stoppingToken), 1, 168);
                await SweepAsync(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RetentionWorker sweep failed.");
                _health.RecordFailure(ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(TimeSpan interval, CancellationToken ct)
    {
        var batchSize = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Retention.BatchSize, ct), 100, 50_000);
        var sw = Stopwatch.StartNew();
        var deleted = new Dictionary<string, long>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var rule in Rules)
        {
            ct.ThrowIfCancellationRequested();
            var days = Math.Clamp(await _settings.GetAsync<int>(rule.SettingKey, ct), 0, 3650);
            if (days == 0) { deleted[rule.Table] = 0; continue; } // 0 = keep forever

            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(days);
            long total = 0;
            while (!ct.IsCancellationRequested)
            {
                var rows = await conn.ExecuteAsync(new CommandDefinition(
                    rule.Sql, new { Cutoff = cutoff, BatchSize = batchSize }, cancellationToken: ct));
                total += rows;
                if (rows < batchSize) break;
            }
            deleted[rule.Table] = total;
            if (total > 0)
                _logger.LogInformation("RetentionWorker pruned {Count} rows from {Table} older than {Days}d.", total, rule.Table, days);
        }

        sw.Stop();
        _health.RecordRun(deleted, sw.Elapsed, DateTime.UtcNow + interval);
    }
}
