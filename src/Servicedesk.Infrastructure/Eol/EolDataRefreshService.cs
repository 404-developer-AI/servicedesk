using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Infrastructure.Eol;

/// Pulls the two endoflife.date feeds and upserts <c>eol_releases</c>.
/// Uses the project's standard <c>INSERT … ON CONFLICT DO UPDATE …
/// RETURNING id</c> shape so a manual trigger can overlap the scheduled
/// cycle without race-induced duplicates.
public sealed class EolDataRefreshService : IEolDataRefreshService
{
    private readonly IEolDataClient _api;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<EolDataRefreshService> _logger;

    public EolDataRefreshService(
        IEolDataClient api,
        NpgsqlDataSource dataSource,
        IIntegrationAuditLogger audit,
        ILogger<EolDataRefreshService> logger)
    {
        _api = api;
        _dataSource = dataSource;
        _audit = audit;
        _logger = logger;
    }

    public async Task<EolRefreshOutcome> RunOnceAsync(string trigger, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await _audit.LogAsync(new IntegrationAuditEvent(
            Integration: EolEventTypes.Integration,
            EventType: EolEventTypes.RefreshStarted,
            Outcome: IntegrationAuditOutcome.Ok,
            Payload: new { trigger }), ct);

        try
        {
            var windows = await _api.FetchWindowsAsync(ct);
            var server = await _api.FetchWindowsServerAsync(ct);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var windowsCount = await UpsertAsync(connection, windows, ct);
            var serverCount = await UpsertAsync(connection, server, ct);

            stopwatch.Stop();
            var outcome = new EolRefreshOutcome(
                Success: true,
                WindowsRows: windowsCount,
                WindowsServerRows: serverCount,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: null,
                ErrorMessage: null);

            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: EolEventTypes.RefreshCompleted,
                Outcome: IntegrationAuditOutcome.Ok,
                LatencyMs: outcome.LatencyMs,
                Payload: new
                {
                    trigger,
                    windows = outcome.WindowsRows,
                    windowsServer = outcome.WindowsServerRows,
                }), ct);

            return outcome;
        }
        catch (EolApiException ex)
        {
            stopwatch.Stop();
            var outcome = new EolRefreshOutcome(
                Success: false,
                WindowsRows: 0,
                WindowsServerRows: 0,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: ex.UpstreamErrorCode ?? "transport_error",
                ErrorMessage: ex.Message);
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: EolEventTypes.RefreshFailed,
                Outcome: IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: outcome.ErrorCode,
                Payload: new { trigger, message = outcome.ErrorMessage }), ct);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "EOL refresh threw an unexpected exception.");
            var outcome = new EolRefreshOutcome(
                Success: false,
                WindowsRows: 0,
                WindowsServerRows: 0,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "internal_error",
                ErrorMessage: ex.Message);
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: EolEventTypes.RefreshFailed,
                Outcome: IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: "internal_error",
                Payload: new { trigger, message = ex.Message }), ct);
            return outcome;
        }
    }

    private static async Task<int> UpsertAsync(
        NpgsqlConnection connection,
        IReadOnlyList<EolReleaseRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        const string sql = """
            INSERT INTO eol_releases
                (product, cycle, release_label, eol_utc, lts, last_refreshed_utc)
            VALUES
                (@product, @cycle, @releaseLabel, @eolUtc, @lts, now())
            ON CONFLICT (product, cycle) DO UPDATE SET
                release_label      = EXCLUDED.release_label,
                eol_utc            = EXCLUDED.eol_utc,
                lts                = EXCLUDED.lts,
                last_refreshed_utc = now()
            RETURNING product
            """;
        var count = 0;
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                product = row.Product,
                cycle = row.Cycle,
                releaseLabel = row.ReleaseLabel,
                eolUtc = row.EolUtc,
                lts = row.Lts,
            }, cancellationToken: ct));
            count++;
        }
        return count;
    }
}
