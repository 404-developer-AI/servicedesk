using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Persistence;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class TimesheetImportService : ITimesheetImportService
{
    /// Detail-list cap so a 53K-row run can't return megabytes of skip
    /// reasons. The aggregate counts (Received vs Imported) always tell
    /// the true totals; this only bounds the per-row detail.
    private const int MaxSkipDetail = 200;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITimesheetTaskService _tasks;
    private readonly ILogger<TimesheetImportService> _logger;

    public TimesheetImportService(NpgsqlDataSource dataSource, ITimesheetTaskService tasks, ILogger<TimesheetImportService> logger)
    {
        _dataSource = dataSource;
        _tasks = tasks;
        _logger = logger;
    }

    public Task<IReadOnlyList<TimesheetTask>> ListTasksAsync(CancellationToken ct = default) =>
        _tasks.ListAsync(includeArchived: true, ct);

    public async Task<IReadOnlyList<TimesheetImportUser>> ListUsersAsync(CancellationToken ct = default)
    {
        // Inactive accounts are included on purpose: an offboarded
        // employee's historical rows must still attribute to their real
        // user, and the import never logs anyone in so "active" is moot.
        const string sql = """
            SELECT id                AS Id,
                   email             AS Email,
                   role_name         AS Role,
                   is_active         AS IsActive,
                   timesheet_enabled AS TimesheetEnabled
              FROM users
             WHERE role_name IN ('Agent', 'Admin')
             ORDER BY email
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows
            .Select(r => new TimesheetImportUser(r.Id, r.Email, r.Role, r.IsActive, r.TimesheetEnabled))
            .ToList();
    }

    public async Task<TimesheetImportBatchResult> ImportBatchAsync(
        string importSource,
        IReadOnlyList<TimesheetImportRow> rows,
        CancellationToken ct = default)
    {
        var source = (importSource ?? string.Empty).Trim();
        if (source.Length is 0 or > 64)
            throw new ArgumentException("importSource must be 1–64 characters.", nameof(importSource));

        var skipped = new List<TimesheetImportSkip>();
        var valid = new List<TimesheetImportRow>(rows.Count);

        foreach (var r in rows)
        {
            var reason = ValidateRow(r);
            if (reason is not null)
            {
                if (skipped.Count < MaxSkipDetail)
                    skipped.Add(new TimesheetImportSkip(r.ImportRef, reason));
                continue;
            }
            valid.Add(r);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Resolve every distinct Zammad number in this batch in one round
        // trip, then attach the ticket id (or null) per row. Doing it here
        // keeps the bridge (tickets.zammad_ticket_number) in the one place
        // the data actually lives.
        var ticketByNumber = await ResolveTicketsAsync(conn, valid, ct);

        var imported = 0;
        var withoutTicketMatch = 0;

        foreach (var r in valid)
        {
            var number = NormalizeNumber(r.ZammadTicketNumber);
            Guid? ticketId = null;
            if (number is not null)
            {
                if (ticketByNumber.TryGetValue(number, out var tid)) ticketId = tid;
                else withoutTicketMatch++;
            }

            try
            {
                await conn.ExecuteAsync(new CommandDefinition(UpsertSql, new
                {
                    userId = r.UserId,
                    date = r.EntryDate.ToDateTime(TimeOnly.MinValue),
                    start = r.StartMinutes,
                    end = r.EndMinutes,
                    minutes = r.EndMinutes - r.StartMinutes,
                    taskId = r.TaskId,
                    ticketId,
                    description = (r.Description ?? string.Empty).Trim(),
                    invoiced = r.Invoiced,
                    createdBy = r.CreatedByUserId,
                    updatedBy = r.UpdatedByUserId,
                    createdUtc = r.CreatedUtc,
                    updatedUtc = r.UpdatedUtc,
                    source,
                    importRef = r.ImportRef,
                }, cancellationToken: ct));
                imported++;
            }
            catch (PostgresException ex)
            {
                // A row can still fail at the DB boundary — most likely a
                // mapped user/task id that doesn't exist on this install
                // (foreign-key violation). Record it and keep the batch
                // going rather than aborting the whole run.
                if (skipped.Count < MaxSkipDetail)
                    skipped.Add(new TimesheetImportSkip(r.ImportRef, $"db_error:{ex.SqlState}"));
            }
        }

        // Migration batches arrive back-to-back; a per-batch ANALYZE on this
        // one table is milliseconds and keeps the timesheet views fast while
        // the import is still streaming in.
        if (imported > 0)
            await PostgresStatistics.AnalyzeAsync(_dataSource, _logger, ct, "timesheet_entries");

        return new TimesheetImportBatchResult(rows.Count, imported, withoutTicketMatch, skipped);
    }

    private static string? ValidateRow(TimesheetImportRow r)
    {
        if (string.IsNullOrWhiteSpace(r.ImportRef)) return "missing_import_ref";
        if (r.UserId == Guid.Empty) return "missing_user";
        if (r.TaskId == Guid.Empty) return "missing_task";
        // Mirror the DB CHECK so a violating row is reported, not thrown.
        if (r.StartMinutes is < 0 or > 1440) return "start_out_of_range";
        if (r.EndMinutes is < 0 or > 1440) return "end_out_of_range";
        if (r.EndMinutes <= r.StartMinutes) return "end_not_after_start";
        return null;
    }

    private async Task<Dictionary<string, Guid>> ResolveTicketsAsync(
        NpgsqlConnection conn, IReadOnlyList<TimesheetImportRow> rows, CancellationToken ct)
    {
        var numbers = rows
            .Select(r => NormalizeNumber(r.ZammadTicketNumber))
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct()
            .ToArray();

        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (numbers.Length == 0) return map;

        const string sql = """
            SELECT zammad_ticket_number AS Number, id AS Id
              FROM tickets
             WHERE is_deleted = FALSE
               AND zammad_ticket_number = ANY(@numbers)
             ORDER BY created_utc ASC
            """;
        var hits = await conn.QueryAsync<TicketNumberRow>(
            new CommandDefinition(sql, new { numbers }, cancellationToken: ct));

        // First (oldest) ticket wins if a Zammad number ever mapped to more
        // than one local ticket — deterministic and matches "the original".
        foreach (var h in hits)
        {
            if (h.Number is { Length: > 0 } && !map.ContainsKey(h.Number))
                map[h.Number] = h.Id;
        }
        return map;
    }

    private static string? NormalizeNumber(string? raw)
    {
        var n = raw?.Trim();
        return string.IsNullOrEmpty(n) ? null : n;
    }

    private const string UpsertSql = """
        INSERT INTO timesheet_entries (
            user_id, entry_date, start_minutes, end_minutes, minutes,
            task_id, ticket_id, description, invoiced,
            created_by, updated_by, created_utc, updated_utc,
            import_source, import_ref)
        VALUES (
            @userId, @date, @start, @end, @minutes,
            @taskId, @ticketId, @description, @invoiced,
            @createdBy, @updatedBy,
            COALESCE(@createdUtc, now()), COALESCE(@updatedUtc, now()),
            @source, @importRef)
        ON CONFLICT (import_source, import_ref) WHERE import_source IS NOT NULL
        DO UPDATE SET
            user_id       = EXCLUDED.user_id,
            entry_date    = EXCLUDED.entry_date,
            start_minutes = EXCLUDED.start_minutes,
            end_minutes   = EXCLUDED.end_minutes,
            minutes       = EXCLUDED.minutes,
            task_id       = EXCLUDED.task_id,
            ticket_id     = EXCLUDED.ticket_id,
            description   = EXCLUDED.description,
            invoiced      = EXCLUDED.invoiced,
            created_by    = EXCLUDED.created_by,
            updated_by    = EXCLUDED.updated_by,
            created_utc   = EXCLUDED.created_utc,
            updated_utc   = EXCLUDED.updated_utc
        """;

    private sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public bool IsActive { get; set; }
        public bool TimesheetEnabled { get; set; }
    }

    private sealed class TicketNumberRow
    {
        public string? Number { get; set; }
        public Guid Id { get; set; }
    }
}
