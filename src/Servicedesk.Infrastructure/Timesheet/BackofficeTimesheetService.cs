using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Timesheet;

/// One ticket row for the back-office Resolved / CWI tabs. Sealed class with
/// get/set + AS-PascalCase aliases per the project's Dapper conventions.
public sealed class BackofficeTicketRow
{
    public Guid TicketId { get; set; }
    public long TicketNumber { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string? CompanyName { get; set; }

    /// Total registered timesheet minutes on the ticket (all agents, all
    /// time) — drives the clickable hours pill. 0 when nothing is logged.
    public int TotalMinutes { get; set; }

    /// True when a Back-Office reviewer ticked this ticket on this tab.
    public bool BoChecked { get; set; }
    public DateTime? CheckedUtc { get; set; }
    public string? CheckedByEmail { get; set; }

    /// The moment the ticket entered its current status (from the ticket
    /// event log, falling back to creation). Used both for the month filter
    /// and as the secondary sort.
    public DateTime EnteredUtc { get; set; }
}

/// One task's registered minutes for a ticket — feeds the hours-pill
/// breakdown popover. Mirrors the Adsolut tab's per-task shape.
public sealed class BackofficeTaskHoursRow
{
    public string TaskName { get; set; } = string.Empty;
    public bool IsAbsence { get; set; }
    public int Minutes { get; set; }
}

public interface IBackofficeTimesheetService
{
    /// Tickets currently in one of <paramref name="statusIds"/> that entered
    /// that status within [fromUtc, toUtc). When
    /// <paramref name="excludeWithReceipt"/> is true (the Resolved tab),
    /// tickets that already have an Adsolut sales receipt are dropped.
    Task<IReadOnlyList<BackofficeTicketRow>> ListAsync(
        Guid[] statusIds,
        string context,
        bool excludeWithReceipt,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<BackofficeTaskHoursRow>> GetTicketHoursAsync(
        Guid ticketId,
        CancellationToken ct = default);

    /// Tick (insert) or untick (delete) the Back-Office marker for a set of
    /// entities on one tab. <paramref name="entityIds"/> are ticket ids for
    /// the resolved/cwi contexts and Adsolut sales-receipt ids for the
    /// adsolut context. Ticking an already-ticked entity keeps the original
    /// checker. Ids that don't exist in the context's table are ignored.
    Task SetChecksAsync(
        IReadOnlyList<Guid> entityIds,
        string context,
        bool isChecked,
        Guid userId,
        CancellationToken ct = default);
}

public sealed class BackofficeTimesheetService : IBackofficeTimesheetService
{
    private readonly NpgsqlDataSource _dataSource;

    public BackofficeTimesheetService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<BackofficeTicketRow>> ListAsync(
        Guid[] statusIds,
        string context,
        bool excludeWithReceipt,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        // No statuses configured for this tab → nothing to show. Skip the
        // round-trip; `= ANY('{}')` would return empty anyway.
        if (statusIds.Length == 0) return Array.Empty<BackofficeTicketRow>();

        // entered_utc = the most recent StatusChange INTO the ticket's
        // current status (metadata->>'to' carries the target status id),
        // falling back to creation for tickets created straight into the
        // status with no transition event. This is what "entered the status
        // this month" filters on, so multiple Closed-category CWI statuses
        // each get their own correct transition date.
        var receiptClause = excludeWithReceipt
            ? "AND NOT EXISTS (SELECT 1 FROM adsolut_sales_receipts asr WHERE asr.ticket_number = t.number)"
            : string.Empty;

        var sql = $"""
            SELECT t.id        AS TicketId,
                   t.number    AS TicketNumber,
                   t.subject   AS Subject,
                   c.name      AS CompanyName,
                   hrs.total_minutes AS TotalMinutes,
                   (bo.entity_id IS NOT NULL) AS BoChecked,
                   bo.checked_utc AS CheckedUtc,
                   bu.email    AS CheckedByEmail,
                   COALESCE(ent.entered_utc, t.created_utc) AS EnteredUtc
            FROM tickets t
            LEFT JOIN companies c ON c.id = t.company_id
            LEFT JOIN LATERAL (
                SELECT MAX(ev.created_utc) AS entered_utc
                FROM ticket_events ev
                WHERE ev.ticket_id = t.id
                  AND ev.event_type = 'StatusChange'
                  AND ev.metadata->>'to' = t.status_id::text
            ) ent ON TRUE
            LEFT JOIN LATERAL (
                SELECT COALESCE(SUM(e.minutes), 0)::int AS total_minutes
                FROM timesheet_entries e
                WHERE e.ticket_id = t.id
            ) hrs ON TRUE
            LEFT JOIN timesheet_bo_checks bo ON bo.entity_id = t.id AND bo.context = @Context
            LEFT JOIN users bu ON bu.id = bo.checked_by
            WHERE t.is_deleted = FALSE
              AND t.status_id = ANY(@StatusIds)
              AND COALESCE(ent.entered_utc, t.created_utc) >= @FromUtc
              AND COALESCE(ent.entered_utc, t.created_utc) <  @ToUtc
              {receiptClause}
            ORDER BY COALESCE(ent.entered_utc, t.created_utc) DESC, t.number DESC
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BackofficeTicketRow>(new CommandDefinition(
            sql,
            new { StatusIds = statusIds, Context = context, FromUtc = fromUtc, ToUtc = toUtc },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<BackofficeTaskHoursRow>> GetTicketHoursAsync(
        Guid ticketId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT tk.name                        AS TaskName,
                   COALESCE(tk.is_absence, FALSE) AS IsAbsence,
                   SUM(e.minutes)::int            AS Minutes
            FROM timesheet_entries e
            JOIN timesheet_tasks tk ON tk.id = e.task_id
            WHERE e.ticket_id = @TicketId
            GROUP BY tk.name, tk.is_absence
            ORDER BY SUM(e.minutes) DESC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BackofficeTaskHoursRow>(new CommandDefinition(
            sql, new { TicketId = ticketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SetChecksAsync(
        IReadOnlyList<Guid> entityIds,
        string context,
        bool isChecked,
        Guid userId,
        CancellationToken ct = default)
    {
        if (entityIds.Count == 0) return;
        var ids = entityIds.ToArray();
        // The entity table depends on the tab. Whitelisted (not user input)
        // so it is safe to interpolate into the existence guard below.
        var entityTable = context == "adsolut" ? "adsolut_sales_receipts" : "tickets";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        if (isChecked)
        {
            // INSERT-SELECT over the id array, guarded by an existence check
            // so a stale id never creates an orphan row. ON CONFLICT keeps
            // the original checker when a row is already ticked.
            var insert = $"""
                INSERT INTO timesheet_bo_checks (entity_id, context, checked_by, checked_utc)
                SELECT x.id, @Context, @UserId, now()
                FROM unnest(@Ids) AS x(id)
                WHERE EXISTS (SELECT 1 FROM {entityTable} e WHERE e.id = x.id)
                ON CONFLICT (entity_id, context) DO NOTHING
                """;
            await connection.ExecuteAsync(new CommandDefinition(
                insert, new { Ids = ids, Context = context, UserId = userId }, cancellationToken: ct));
        }
        else
        {
            const string delete = """
                DELETE FROM timesheet_bo_checks
                WHERE context = @Context AND entity_id = ANY(@Ids)
                """;
            await connection.ExecuteAsync(new CommandDefinition(
                delete, new { Ids = ids, Context = context }, cancellationToken: ct));
        }
    }
}
