using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Performance;

/// Periodic PostgreSQL statistics snapshots. The views are cumulative, so
/// the dashboard computes deltas between two snapshots for any period —
/// pg_stat_statements is never reset. Statement + database snapshots run
/// every Performance.PgSnapshotIntervalMinutes (1 min in Diagnose); table
/// snapshots run hourly (they also feed the table-growth chart).
public sealed class PgSnapshotService : BackgroundService
{
    private readonly PgInspector _inspector;
    private readonly IPerfSettings _settings;
    private readonly ILogger<PgSnapshotService> _logger;
    private DateTimeOffset _lastStatements = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTablesCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAccessCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastErrorLog = DateTimeOffset.MinValue;

    public PgSnapshotService(PgInspector inspector, IPerfSettings settings, ILogger<PgSnapshotService> logger)
    {
        _inspector = inspector;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _settings.Time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastErrorLog > TimeSpan.FromMinutes(30))
                {
                    _lastErrorLog = now;
                    _logger.LogWarning(ex, "PostgreSQL statistics snapshot failed.");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (!_settings.IsEnabled(PerfCollector.Postgres)) return;
        // Attributed like any worker, so the monitor's own database cost is
        // visible on its dashboard ("worker:perf-snapshots").
        PerfContext.Worker = "perf-snapshots";
        var now = _settings.Time.GetUtcNow();

        if (now - _lastAccessCheck > TimeSpan.FromMinutes(10))
        {
            await _inspector.CheckAccessAsync(ct);
            _lastAccessCheck = now;
        }

        var options = _settings.Options;
        var interval = TimeSpan.FromMinutes(_settings.Level == PerfLevel.Diagnose
            ? options.PgSnapshotDiagnoseIntervalMinutes
            : options.PgSnapshotIntervalMinutes);
        if (now - _lastStatements >= interval - TimeSpan.FromSeconds(5))
        {
            _lastStatements = now;
            if (_inspector.Access.StatStatements)
            {
                try
                {
                    await _inspector.SnapshotStatementsAsync(now, ct);
                }
                catch (PostgresException ex)
                {
                    _logger.LogDebug(ex, "pg_stat_statements snapshot skipped.");
                    _lastAccessCheck = DateTimeOffset.MinValue; // re-check access next round
                }
            }
            await _inspector.SnapshotDatabaseAsync(now, ct);
        }

        // v0.1.26 — table snapshots every 60 min (15 in Diagnose), measured
        // against the newest STORED snapshot rather than an in-memory clock:
        // after a restart or "Delete all measurements" a fresh baseline is
        // taken at once. Period deltas need a snapshot at (or before) the
        // start of the period; without one the report used to fall back to
        // the cumulative counters since the last statistics reset.
        var tablesEvery = TimeSpan.FromMinutes(_settings.Level == PerfLevel.Diagnose ? 15 : 60);
        if (now - _lastTablesCheck >= TimeSpan.FromMinutes(1))
        {
            _lastTablesCheck = now;
            var last = await _inspector.LastTableSnapshotAsync(ct);
            if (last is null || now - last.Value >= tablesEvery - TimeSpan.FromSeconds(30))
            {
                await _inspector.SnapshotTablesAsync(now, ct);
            }
        }
    }
}
