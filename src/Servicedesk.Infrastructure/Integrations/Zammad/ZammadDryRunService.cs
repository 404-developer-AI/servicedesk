using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Dapper-backed implementation of <see cref="IZammadDryRunService"/>.
/// Two halves:
/// <list type="bullet">
/// <item>Start flow — write the <c>zammad_import_runs</c> row, enqueue
/// the run id on the background queue, return the id.</item>
/// <item>Read flow — runs-list, run-detail, records-page.</item>
/// </list>
/// The worker itself lives in <see cref="ZammadDryRunWorker"/>; this
/// service stays I/O only so it stays unit-testable without the
/// background scaffolding.
public sealed class ZammadDryRunService : IZammadDryRunService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IZammadDryRunQueue _queue;
    private readonly IZammadMappingService _mappings;
    private readonly IZammadTicketResolver _resolver;
    private readonly ILogger<ZammadDryRunService> _logger;

    public ZammadDryRunService(
        NpgsqlDataSource dataSource,
        IZammadDryRunQueue queue,
        IZammadMappingService mappings,
        IZammadTicketResolver resolver,
        ILogger<ZammadDryRunService> logger)
    {
        _dataSource = dataSource;
        _queue = queue;
        _mappings = mappings;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<Guid> StartDryRunAsync(
        ZammadImportSourceFilter filter,
        Guid? startedByUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // PlannedTotal seeds the progress UI with a denominator while the
        // worker walks the upstream. When the picker passed an explicit
        // ticket-id list we know it up-front; otherwise we leave it null
        // and the worker fills it after the first /tickets/search page.
        var plannedTotal = filter.TicketIds?.Count;
        var totals = ZammadImportTotals.Empty(plannedTotal);

        const string sql = """
            INSERT INTO zammad_import_runs
                (kind, status, started_by_user_id, started_utc, source_filter, totals)
            VALUES
                ('dry_run', 'pending', @StartedByUserId, now(), @Filter::jsonb, @Totals::jsonb)
            RETURNING id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            StartedByUserId = startedByUserId,
            Filter = JsonSerializer.Serialize(filter),
            Totals = JsonSerializer.Serialize(totals),
        }, cancellationToken: ct));

        if (!_queue.TryEnqueue(id))
        {
            // Queue is bounded at 32; under normal admin use the worker
            // drains in seconds and this branch never fires. When it does,
            // the run row stays in 'pending' — the worker picks it up on
            // its next sweep after the queue drains. We bubble a soft
            // warning to logs so an operator notices the unusual rate.
            _logger.LogWarning(
                "Zammad dry-run queue refused enqueue for run {RunId}; row remains pending.",
                id);
        }

        return id;
    }

    public async Task<IReadOnlyList<ZammadImportRunSummary>> GetRunsAsync(
        int limit,
        CancellationToken ct)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        const string sql = """
            SELECT r.id                                   AS "Id",
                   r.kind                                 AS "Kind",
                   r.status                               AS "Status",
                   r.started_by_user_id                   AS "StartedByUserId",
                   u.email                                AS "StartedByDisplayName",
                   r.started_utc                          AS "StartedUtc",
                   r.finished_utc                         AS "FinishedUtc",
                   r.totals::text                         AS "TotalsJson",
                   r.error_message                        AS "ErrorMessage"
              FROM zammad_import_runs r
              LEFT JOIN users u ON u.id = r.started_by_user_id
             ORDER BY r.started_utc DESC, r.id DESC
             LIMIT @Limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RunRow>(new CommandDefinition(
            sql, new { Limit = clamped }, cancellationToken: ct));
        return rows.Select(MapRunRow).ToList();
    }

    public async Task<ZammadImportRunDetail?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id                                   AS "Id",
                   r.kind                                 AS "Kind",
                   r.status                               AS "Status",
                   r.started_by_user_id                   AS "StartedByUserId",
                   u.email                                AS "StartedByDisplayName",
                   r.started_utc                          AS "StartedUtc",
                   r.finished_utc                         AS "FinishedUtc",
                   r.totals::text                         AS "TotalsJson",
                   r.error_message                        AS "ErrorMessage",
                   r.source_filter::text                  AS "SourceFilterJson"
              FROM zammad_import_runs r
              LEFT JOIN users u ON u.id = r.started_by_user_id
             WHERE r.id = @RunId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RunDetailRow>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
        if (row is null) return null;

        var summary = MapRunRow(row);
        ZammadImportSourceFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(row.SourceFilterJson))
        {
            try { filter = JsonSerializer.Deserialize<ZammadImportSourceFilter>(row.SourceFilterJson); }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialize source_filter on run {RunId}; returning null filter.", runId);
            }
        }
        return new ZammadImportRunDetail(summary, filter);
    }

    public async Task<ZammadImportRecordPage> GetRecordsAsync(
        Guid runId,
        Guid? cursor,
        int limit,
        string? resultFilter,
        CancellationToken ct)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        var parameters = new DynamicParameters();
        parameters.Add("RunId", runId);
        parameters.Add("Limit", clamped);

        var sql = """
            SELECT id                       AS "Id",
                   zammad_ticket_id         AS "ZammadTicketId",
                   zammad_ticket_number     AS "ZammadTicketNumber",
                   zammad_ticket_title      AS "ZammadTicketTitle",
                   result                   AS "Result",
                   unresolved_reasons       AS "UnresolvedReasons",
                   mapping::text            AS "MappingJson",
                   would_create_ticket_id   AS "WouldCreateTicketId",
                   created_utc              AS "CreatedUtc"
              FROM zammad_import_records
             WHERE run_id = @RunId
            """;
        if (!string.IsNullOrWhiteSpace(resultFilter))
        {
            sql += " AND result = @ResultFilter";
            parameters.Add("ResultFilter", resultFilter);
        }
        if (cursor is not null)
        {
            // Keyset pagination on id. Records share a run_id so id alone
            // is enough — no need for the (utc, id) compound the integration-
            // audit reader uses.
            sql += " AND id > @Cursor";
            parameters.Add("Cursor", cursor);
        }
        sql += " ORDER BY id ASC LIMIT @Limit";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<RecordRow>(new CommandDefinition(
            sql, parameters, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new ZammadImportRecordItem(
            Id: r.Id,
            ZammadTicketId: r.ZammadTicketId,
            ZammadTicketNumber: r.ZammadTicketNumber,
            ZammadTicketTitle: r.ZammadTicketTitle,
            Result: r.Result,
            UnresolvedReasons: r.UnresolvedReasons ?? Array.Empty<string>(),
            MappingJson: r.MappingJson ?? "{}",
            WouldCreateTicketId: r.WouldCreateTicketId,
            CreatedUtc: r.CreatedUtc)).ToList();
        var nextCursor = items.Count == clamped ? items[^1].Id : (Guid?)null;
        return new ZammadImportRecordPage(items, nextCursor);
    }

    public async Task<int> RecheckRecordsAsync(
        Guid runId,
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken ct)
    {
        if (recordIds.Count == 0) return 0;

        // Load mappings once — same as the worker pre-loads at run-
        // start. Recheck is admin-driven and rare so per-call load is
        // fine.
        var dict = await _mappings.LoadDictionaryAsync(ct);

        // Pull the rows we're allowed to mutate. The WHERE binds
        // run_id + ANY(@Ids) so a request with a foreign run-id can't
        // overwrite someone else's records.
        const string loadSql = """
            SELECT id                AS "Id",
                   zammad_ticket_id  AS "ZammadTicketId",
                   result            AS "Result"
              FROM zammad_import_records
             WHERE run_id = @RunId
               AND id = ANY(@Ids)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var existing = (await conn.QueryAsync<RecheckTargetRow>(new CommandDefinition(
            loadSql, new { RunId = runId, Ids = recordIds.ToArray() }, cancellationToken: ct))).ToList();
        if (existing.Count == 0) return 0;

        // Compute deltas as we go so a single UPDATE on the run row
        // covers all rechecks. result-decrement is keyed on the old
        // result, increment on the new.
        var deltas = new Dictionary<string, int>();
        var rechecked = 0;
        foreach (var row in existing)
        {
            ct.ThrowIfCancellationRequested();
            var resolved = await _resolver.ResolveAsync(row.ZammadTicketId, dict, ct);

            // Persist the new verdict on this record. The same row id
            // stays so the records-page URL + any open dialogs survive.
            const string updateSql = """
                UPDATE zammad_import_records
                   SET zammad_ticket_number = @ZammadTicketNumber,
                       zammad_ticket_title  = @ZammadTicketTitle,
                       result               = @Result,
                       unresolved_reasons   = @UnresolvedReasons,
                       mapping              = @Mapping::jsonb,
                       created_utc          = now()
                 WHERE id = @Id
                """;
            await conn.ExecuteAsync(new CommandDefinition(updateSql, new
            {
                Id = row.Id,
                ZammadTicketNumber = resolved.ZammadTicketNumber,
                ZammadTicketTitle = resolved.ZammadTicketTitle,
                Result = resolved.Result,
                UnresolvedReasons = resolved.UnresolvedReasons.ToArray(),
                Mapping = JsonSerializer.Serialize(resolved.Mapping),
            }, cancellationToken: ct));

            // Track delta — old gets -1, new gets +1; net zero when the
            // verdict didn't change.
            if (!string.Equals(row.Result, resolved.Result, StringComparison.Ordinal))
            {
                deltas[row.Result] = deltas.GetValueOrDefault(row.Result, 0) - 1;
                deltas[resolved.Result] = deltas.GetValueOrDefault(resolved.Result, 0) + 1;
            }
            rechecked++;
        }

        // Apply totals deltas in a single UPDATE so the run row reflects
        // the new verdict-mix even on a long recheck. processed is
        // unchanged — every recheck overwrites an existing record, no
        // new ones are added.
        if (deltas.Count > 0)
        {
            await ApplyTotalsDeltaAsync(conn, runId, deltas, ct);
        }

        return rechecked;
    }

    private static async Task ApplyTotalsDeltaAsync(
        NpgsqlConnection conn,
        Guid runId,
        IReadOnlyDictionary<string, int> deltas,
        CancellationToken ct)
    {
        // Read-modify-write of the JSONB totals. Recheck is admin-
        // driven (one human, not parallel workers) so a row-level race
        // is highly unlikely; we rely on the surrounding HTTP request
        // being the only writer on this column for the duration.
        const string read = "SELECT totals::text FROM zammad_import_runs WHERE id = @RunId FOR UPDATE";
        await using var tx = await conn.BeginTransactionAsync(ct);
        var totalsJson = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            read, new { RunId = runId }, transaction: tx, cancellationToken: ct));
        var totals = string.IsNullOrWhiteSpace(totalsJson)
            ? ZammadImportTotals.Empty(null)
            : (JsonSerializer.Deserialize<ZammadImportTotals>(totalsJson) ?? ZammadImportTotals.Empty(null));

        foreach (var (key, delta) in deltas)
        {
            totals = key switch
            {
                ZammadImportRecordResult.Mapped =>
                    totals with { Mapped = Math.Max(0, totals.Mapped + delta) },
                ZammadImportRecordResult.SkippedNoContact =>
                    totals with { SkippedNoContact = Math.Max(0, totals.SkippedNoContact + delta) },
                ZammadImportRecordResult.SkippedNoGroupMapping =>
                    totals with { SkippedNoGroupMapping = Math.Max(0, totals.SkippedNoGroupMapping + delta) },
                ZammadImportRecordResult.SkippedNoStateMapping =>
                    totals with { SkippedNoStateMapping = Math.Max(0, totals.SkippedNoStateMapping + delta) },
                ZammadImportRecordResult.SkippedNoPriorityMapping =>
                    totals with { SkippedNoPriorityMapping = Math.Max(0, totals.SkippedNoPriorityMapping + delta) },
                _ => totals with { Failed = Math.Max(0, totals.Failed + delta) },
            };
        }

        const string write = "UPDATE zammad_import_runs SET totals = @Totals::jsonb WHERE id = @RunId";
        await conn.ExecuteAsync(new CommandDefinition(write, new
        {
            RunId = runId,
            Totals = JsonSerializer.Serialize(totals),
        }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<bool> CancelRunAsync(Guid runId, CancellationToken ct)
    {
        // Only flip pending/running rows — a completed/failed/cancelled
        // row stays where it is so admins can't accidentally retro-
        // cancel old runs. The worker reads the status column between
        // tickets and exits cleanly on next poll.
        const string sql = """
            UPDATE zammad_import_runs
               SET status = 'cancelled',
                   finished_utc = COALESCE(finished_utc, now())
             WHERE id = @RunId
               AND status IN ('pending','running')
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
        return n > 0;
    }

    // ---- row mapping --------------------------------------------------

    private static ZammadImportRunSummary MapRunRow(RunRow row)
    {
        var totals = ParseTotals(row.TotalsJson);
        return new ZammadImportRunSummary(
            Id: row.Id,
            Kind: ParseKind(row.Kind),
            Status: ParseStatus(row.Status),
            StartedByUserId: row.StartedByUserId,
            StartedByDisplayName: row.StartedByDisplayName,
            StartedUtc: row.StartedUtc,
            FinishedUtc: row.FinishedUtc,
            Totals: totals,
            ErrorMessage: row.ErrorMessage);
    }

    private static ZammadImportTotals ParseTotals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ZammadImportTotals.Empty(null);
        try
        {
            return JsonSerializer.Deserialize<ZammadImportTotals>(json)
                ?? ZammadImportTotals.Empty(null);
        }
        catch (JsonException)
        {
            return ZammadImportTotals.Empty(null);
        }
    }

    private static ZammadImportRunKind ParseKind(string kind) => kind switch
    {
        "dry_run" => ZammadImportRunKind.DryRun,
        "import"  => ZammadImportRunKind.Import,
        _ => ZammadImportRunKind.DryRun,
    };

    private static ZammadImportRunStatus ParseStatus(string status) => status switch
    {
        "pending"   => ZammadImportRunStatus.Pending,
        "running"   => ZammadImportRunStatus.Running,
        "completed" => ZammadImportRunStatus.Completed,
        "failed"    => ZammadImportRunStatus.Failed,
        "cancelled" => ZammadImportRunStatus.Cancelled,
        _ => ZammadImportRunStatus.Pending,
    };

    // ---- Row-DTOs (Dapper convention: sealed class { get; set; }) -----

    private class RunRow
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid? StartedByUserId { get; set; }
        public string? StartedByDisplayName { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string? TotalsJson { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed class RunDetailRow : RunRow
    {
        public string? SourceFilterJson { get; set; }
    }

    private sealed class RecheckTargetRow
    {
        public Guid Id { get; set; }
        public long ZammadTicketId { get; set; }
        public string Result { get; set; } = string.Empty;
    }

    private sealed class RecordRow
    {
        public Guid Id { get; set; }
        public long ZammadTicketId { get; set; }
        public string? ZammadTicketNumber { get; set; }
        public string? ZammadTicketTitle { get; set; }
        public string Result { get; set; } = string.Empty;
        public string[]? UnresolvedReasons { get; set; }
        public string? MappingJson { get; set; }
        public Guid? WouldCreateTicketId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
