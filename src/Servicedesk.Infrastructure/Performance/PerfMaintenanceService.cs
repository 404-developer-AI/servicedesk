using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

/// Housekeeping for the monitor's own tables, every five minutes:
/// <list type="bullet">
/// <item>roll completed hours from the *_minute tables into the *_hour twins
///   (idempotent: an hour is deleted and rebuilt, so a crashed run simply
///   repeats);</item>
/// <item>prune each table by its retention setting, in small batches;</item>
/// <item>optional alerts: critical findings become Health incidents, at most
///   once per finding per cooldown window;</item>
/// <item>on start, a "deploy" timeline marker when the app version changed.</item>
/// </list>
public sealed class PerfMaintenanceService : BackgroundService
{
    private static readonly (string Minute, string Hour, string Keys, string Aggregates)[] Rollups =
    {
        ("perf_http_minute", "perf_http_hour", "method, route, status_class",
            "sum(count), sum(err_count), sum(sum_ms), max(max_ms), perf_hist_sum(hist), sum(db_count), sum(db_ms), sum(ext_ms), " +
            "sum(pipeline_ms), sum(bytes_sum), perf_hist_sum(size_hist), max(users)"),
        ("perf_db_query_minute", "perf_db_query_hour", "fingerprint, source",
            "sum(count), sum(err_count), sum(sum_ms), max(max_ms), perf_hist_sum(hist)"),
        ("perf_span_minute", "perf_span_hour", "kind, name, detail",
            "sum(count), sum(err_count), sum(sum_ms), max(max_ms), perf_hist_sum(hist)"),
        ("perf_rum_minute", "perf_rum_hour", "route, metric, detail, device, connection",
            "sum(count), sum(sum_value), max(max_value), perf_hist_sum(hist)"),
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IPerfSettings _perf;
    private readonly PerfDatasetBuilder _datasets;
    private readonly PerfQueries _queries;
    private readonly IIncidentLog _incidents;
    private readonly ILogger<PerfMaintenanceService> _logger;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAlertEval = DateTimeOffset.MinValue;

    public PerfMaintenanceService(
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        IPerfSettings perf,
        PerfDatasetBuilder datasets,
        PerfQueries queries,
        IIncidentLog incidents,
        ILogger<PerfMaintenanceService> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _perf = perf;
        _datasets = datasets;
        _queries = queries;
        _incidents = incidents;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Attributed like any worker so the monitor's own database cost shows
        // up as "worker:perf-maintenance" rather than "other".
        PerfContext.Worker = "perf-maintenance";
        await Safe(() => DeployMarkerAsync(stoppingToken), "deploy marker");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), _perf.Time);
        do
        {
            await Safe(() => RollupAsync(stoppingToken), "hourly rollup");
            var now = _perf.Time.GetUtcNow();
            if (now - _lastPrune > TimeSpan.FromHours(1))
            {
                _lastPrune = now;
                await Safe(() => PruneAsync(stoppingToken), "retention");
            }
            if (now - _lastAlertEval > TimeSpan.FromMinutes(15))
            {
                _lastAlertEval = now;
                await Safe(() => AlertAsync(stoppingToken), "alerts");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task Safe(Func<Task> work, string label)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Performance maintenance step '{Step}' failed.", label);
        }
    }

    private async Task DeployMarkerAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var label = "Started version " + PerfAppInfo.Version;
        var last = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT label FROM perf_marker WHERE kind = 'deploy' ORDER BY ts_utc DESC LIMIT 1", cancellationToken: ct));
        if (last == label) return;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO perf_marker (kind, label) VALUES ('deploy', @Label)", new { Label = label }, cancellationToken: ct));
    }

    internal async Task RollupAsync(CancellationToken ct)
    {
        var now = _perf.Time.GetUtcNow().UtcDateTime;
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rolledUntil = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT value_utc FROM perf_rollup_state WHERE name = 'hour'", cancellationToken: ct));
        if (rolledUntil is null)
        {
            var earliest = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT min(bucket_utc) FROM perf_http_minute", cancellationToken: ct));
            rolledUntil = earliest is null ? currentHour : new DateTime(earliest.Value.Year, earliest.Value.Month, earliest.Value.Day, earliest.Value.Hour, 0, 0, DateTimeKind.Utc);
        }

        // Roll each completed hour (at most 48 per pass to bound the work).
        var hour = rolledUntil.Value;
        var done = 0;
        while (hour < currentHour && done < 48)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var (minute, hourTable, keys, aggregates) in Rollups)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {hourTable} WHERE bucket_utc = @Hour", new { Hour = hour }, tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition($"""
                    INSERT INTO {hourTable}
                    SELECT @Hour, {keys}, {aggregates}
                    FROM {minute}
                    WHERE bucket_utc >= @Hour AND bucket_utc < @Next
                    GROUP BY {keys}
                    """, new { Hour = hour, Next = hour.AddHours(1) }, tx, commandTimeout: 120, cancellationToken: ct));
            }
            hour = hour.AddHours(1);
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO perf_rollup_state (name, value_utc) VALUES ('hour', @Until)
                ON CONFLICT (name) DO UPDATE SET value_utc = EXCLUDED.value_utc
                """, new { Until = hour }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            done++;
        }
    }

    internal async Task PruneAsync(CancellationToken ct)
    {
        var minuteDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.RetentionMinuteDays, ct), 1, 90);
        var hourDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.RetentionHourDays, ct), 7, 730);
        var eventDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.RetentionEventDays, ct), 1, 90);
        var pgDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.RetentionPgSnapshotDays, ct), 1, 90);
        var tableDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.RetentionTableSnapshotDays, ct), 7, 730);
        var now = _perf.Time.GetUtcNow().UtcDateTime;

        var rules = new (string Table, string Column, int Days)[]
        {
            ("perf_http_minute", "bucket_utc", minuteDays),
            ("perf_db_query_minute", "bucket_utc", minuteDays),
            ("perf_span_minute", "bucket_utc", minuteDays),
            ("perf_rum_minute", "bucket_utc", minuteDays),
            ("perf_http_hour", "bucket_utc", hourDays),
            ("perf_db_query_hour", "bucket_utc", hourDays),
            ("perf_span_hour", "bucket_utc", hourDays),
            ("perf_rum_hour", "bucket_utc", hourDays),
            // Runtime minutes carry the version per minute and back the long
            // charts — kept as long as the hourly rollups (1 row per minute).
            ("perf_runtime_minute", "bucket_utc", hourDays),
            ("perf_nplusone_minute", "bucket_utc", eventDays),
            ("perf_slow_request", "ts_utc", eventDays),
            ("perf_worker_run", "started_utc", eventDays),
            ("perf_pg_statement_snapshot", "ts_utc", pgDays),
            ("perf_pg_db_snapshot", "ts_utc", pgDays),
            ("perf_pg_table_snapshot", "ts_utc", tableDays),
            ("perf_marker", "ts_utc", Math.Max(hourDays, 365)),
            ("perf_sql_fingerprint", "last_seen_utc", hourDays),
            ("perf_pg_query_text", "last_seen_utc", pgDays),
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var (table, column, days) in rules)
        {
            var cutoff = now.AddDays(-days);
            // Batched by ctid so a first prune after a long gap never holds a
            // big lock or one huge transaction.
            for (var i = 0; i < 200; i++)
            {
                var deleted = await conn.ExecuteAsync(new CommandDefinition($"""
                    DELETE FROM {table} WHERE ctid = ANY(ARRAY(
                        SELECT ctid FROM {table} WHERE {column} < @Cutoff LIMIT 20000))
                    """, new { Cutoff = cutoff }, commandTimeout: 120, cancellationToken: ct));
                if (deleted < 20000) break;
            }
        }
    }

    private async Task AlertAsync(CancellationToken ct)
    {
        if (!await _settings.GetAsync<bool>(SettingKeys.Performance.AlertsEnabled, ct)) return;
        if (_perf.Level == PerfLevel.Off) return;
        var cooldown = TimeSpan.FromHours(Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Performance.AlertsCooldownHours, ct), 1, 168));
        var now = _perf.Time.GetUtcNow();
        var dataset = await _datasets.BuildAsync(now.AddMinutes(-30), now, includeLive: true, compare: false, ct);
        var critical = PerfFindings.Evaluate(dataset).Where(f => f.Severity == PerfSeverity.Critical).ToList();
        if (critical.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var finding in critical.Take(5))
        {
            var last = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT last_alert_utc FROM perf_alert_state WHERE finding_key = @Key", new { Key = finding.Key }, cancellationToken: ct));
            if (last is not null && now.UtcDateTime - last.Value < cooldown) continue;

            await _incidents.ReportAsync(
                "performance",
                IncidentSeverity.Critical,
                PerfRedactor.Text(finding.Title),
                PerfRedactor.Text(finding.Action),
                JsonSerializer.Serialize(new { finding.Key, finding.Category, evidence = finding.Evidence.Select(e => $"{e.Label}: {e.Value}") }),
                ct);
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO perf_alert_state (finding_key, last_alert_utc) VALUES (@Key, @Now)
                ON CONFLICT (finding_key) DO UPDATE SET last_alert_utc = EXCLUDED.last_alert_utc
                """, new { Key = finding.Key, Now = now.UtcDateTime }, cancellationToken: ct));
        }
    }
}
