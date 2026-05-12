using System.Text;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class ManagerTimesheetService : IManagerTimesheetService
{
    /// Same projection as the own-row service, plus the agent's email so
    /// the manager grid can show "who" on every row.
    private const string SelectFromJoin = """
        SELECT  e.id              AS Id,
                e.user_id         AS UserId,
                u.email           AS UserEmail,
                e.entry_date      AS EntryDate,
                e.start_minutes   AS StartMinutes,
                e.end_minutes     AS EndMinutes,
                e.minutes         AS Minutes,
                e.task_id         AS TaskId,
                t.name            AS TaskName,
                t.requires_ticket AS TaskRequiresTicket,
                t.is_absence      AS TaskIsAbsence,
                e.ticket_id       AS TicketId,
                k.number          AS TicketNumber,
                k.subject         AS TicketSubject,
                k.company_id      AS CompanyId,
                c.name            AS CompanyName,
                e.description     AS Description,
                e.invoiced        AS Invoiced,
                e.created_utc     AS CreatedUtc,
                e.updated_utc     AS UpdatedUtc
        FROM timesheet_entries e
        JOIN timesheet_tasks   t ON t.id = e.task_id
        JOIN users             u ON u.id = e.user_id
        LEFT JOIN tickets      k ON k.id = e.ticket_id
        LEFT JOIN companies    c ON c.id = k.company_id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITimesheetTaskService _tasks;

    public ManagerTimesheetService(NpgsqlDataSource dataSource, ITimesheetTaskService tasks)
    {
        _dataSource = dataSource;
        _tasks = tasks;
    }

    public async Task<IReadOnlyList<TimesheetEntryRow>> ListAsync(
        ManagerEntryFilter filter, CancellationToken ct = default)
    {
        // Default range = last 7 days inclusive. Prevents an empty filter
        // from yielding "all rows in the database" on a busy install.
        var from = filter.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6));
        var to = filter.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (to < from) (from, to) = (to, from);

        var limit = filter.Limit <= 0 ? 500 : Math.Min(filter.Limit, 2000);
        var sql = new StringBuilder(SelectFromJoin);
        sql.AppendLine();
        sql.AppendLine("WHERE e.entry_date BETWEEN @from AND @to");

        var p = new DynamicParameters();
        p.Add("from", from.ToDateTime(TimeOnly.MinValue));
        p.Add("to", to.ToDateTime(TimeOnly.MinValue));

        if (filter.UserId is { } uid && uid != Guid.Empty)
        {
            sql.AppendLine("  AND e.user_id = @userId");
            p.Add("userId", uid);
        }
        if (filter.TicketId is { } tid && tid != Guid.Empty)
        {
            sql.AppendLine("  AND e.ticket_id = @ticketId");
            p.Add("ticketId", tid);
        }
        if (filter.TaskId is { } taskId && taskId != Guid.Empty)
        {
            sql.AppendLine("  AND e.task_id = @taskId");
            p.Add("taskId", taskId);
        }

        var trimmedSearch = (filter.Search ?? string.Empty).Trim();
        if (trimmedSearch.Length > 0)
        {
            // Free-text on company name, contact name (via tickets.requester_contact_id),
            // ticket subject, and the entry's own description. ILIKE is fine
            // here because the dataset per manager session is small.
            sql.AppendLine("""
                  AND (
                       c.name        ILIKE @search
                    OR k.subject     ILIKE @search
                    OR e.description ILIKE @search
                    OR u.email       ILIKE @search
                    OR EXISTS (
                       SELECT 1 FROM contacts ct
                       WHERE ct.id = k.requester_contact_id
                         AND (
                              ct.first_name ILIKE @search
                           OR ct.last_name  ILIKE @search
                           OR ct.email      ILIKE @search
                           OR (ct.first_name || ' ' || ct.last_name) ILIKE @search))
                  )
                """);
            p.Add("search", "%" + EscapeLike(trimmedSearch) + "%");
        }

        sql.AppendLine("ORDER BY e.entry_date DESC, e.start_minutes ASC, u.email ASC");
        sql.AppendLine("LIMIT @limit");
        p.Add("limit", limit);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimesheetEntryRow>(
            new CommandDefinition(sql.ToString(), p, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TimesheetUser>> ListTimesheetUsersAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id      AS Id,
                   email   AS Email,
                   timesheet_enabled AS Enabled,
                   timesheet_manager AS Manager
            FROM users
            WHERE (timesheet_enabled = TRUE OR timesheet_manager = TRUE)
              AND role_name IN ('Agent', 'Admin')
              AND is_active = TRUE
            ORDER BY email
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimesheetUserRow>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new TimesheetUser(r.Id, r.Email, r.Enabled, r.Manager)).ToList();
    }

    public async Task<TimesheetEntryRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var sql = SelectFromJoin + "\nWHERE e.id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TimesheetEntryRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<ManagerUpdateResult> UpdateAsync(
        Guid id, TimesheetEntryInput input, CancellationToken ct = default)
    {
        var before = await GetAsync(id, ct);
        if (before is null) return new ManagerUpdateResult.NotFound();

        var errors = await ValidateAsync(input, ct);
        if (errors.Count > 0) return new ManagerUpdateResult.ValidationFailed(errors);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE timesheet_entries SET
                entry_date    = @date,
                start_minutes = @start,
                end_minutes   = @end,
                minutes       = @minutes,
                task_id       = @taskId,
                ticket_id     = @ticketId,
                description   = @description,
                updated_utc   = now()
            WHERE id = @id
            """,
            new
            {
                id,
                date = input.EntryDate.ToDateTime(TimeOnly.MinValue),
                start = input.StartMinutes,
                end = input.EndMinutes,
                minutes = input.EndMinutes - input.StartMinutes,
                taskId = input.TaskId,
                ticketId = input.TicketId,
                description = input.Description.Trim(),
            },
            cancellationToken: ct));
        if (rows == 0) return new ManagerUpdateResult.NotFound();

        var after = await GetAsync(id, ct);
        return new ManagerUpdateResult.Updated(before, after!);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM timesheet_entries WHERE id = @id",
            new { id },
            cancellationToken: ct));
        return rows > 0;
    }

    public async Task<MonthRollup> GetMonthAsync(
        Guid userId, int year, int month, CancellationToken ct = default)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (year is < 1900 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));

        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var email = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT email FROM users WHERE id = @id",
            new { id = userId }, cancellationToken: ct)) ?? "";

        const string sql = """
            SELECT  e.entry_date  AS EntryDate,
                    e.task_id     AS TaskId,
                    t.name        AS TaskName,
                    t.is_absence  AS IsAbsence,
                    SUM(e.minutes)::int AS Minutes
            FROM timesheet_entries e
            JOIN timesheet_tasks   t ON t.id = e.task_id
            WHERE e.user_id = @userId
              AND e.entry_date BETWEEN @from AND @to
            GROUP BY e.entry_date, e.task_id, t.name, t.is_absence
            ORDER BY e.entry_date ASC, t.is_absence ASC, t.name ASC
            """;

        var rows = await conn.QueryAsync<MonthDayBreakdownRow>(
            new CommandDefinition(sql,
                new
                {
                    userId,
                    from = first.ToDateTime(TimeOnly.MinValue),
                    to = last.ToDateTime(TimeOnly.MinValue),
                },
                cancellationToken: ct));

        var byDate = rows
            .GroupBy(r => DateOnly.FromDateTime(r.EntryDate))
            .ToDictionary(g => g.Key, g => g.ToList());

        var days = new List<MonthDayRollup>(DateTime.DaysInMonth(year, month));
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            if (byDate.TryGetValue(d, out var list))
            {
                var work = list.Where(r => !r.IsAbsence).Sum(r => r.Minutes);
                var absence = list.Where(r => r.IsAbsence).Sum(r => r.Minutes);
                var breakdown = list
                    .Select(r => new MonthDayBreakdown(r.TaskId, r.TaskName, r.IsAbsence, r.Minutes))
                    .ToList();
                days.Add(new MonthDayRollup(d, work, absence, list.Count, breakdown));
            }
            else
            {
                days.Add(new MonthDayRollup(d, 0, 0, 0, Array.Empty<MonthDayBreakdown>()));
            }
        }

        return new MonthRollup(userId, email, year, month, days);
    }

    private async Task<List<TimesheetFieldError>> ValidateAsync(
        TimesheetEntryInput input, CancellationToken ct)
    {
        var errors = new List<TimesheetFieldError>();

        if (input.StartMinutes is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("startMinutes", "Start time is out of range."));
        if (input.EndMinutes is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("endMinutes", "End time is out of range."));
        if (input.EndMinutes <= input.StartMinutes)
            errors.Add(new TimesheetFieldError("endMinutes", "End time must be after start time."));
        if (string.IsNullOrWhiteSpace(input.Description))
            errors.Add(new TimesheetFieldError("description", "Description is required."));
        if (input.TaskId == Guid.Empty)
        {
            errors.Add(new TimesheetFieldError("taskId", "Task is required."));
            return errors;
        }

        var task = await _tasks.GetAsync(input.TaskId, ct);
        if (task is null || task.Archived)
        {
            errors.Add(new TimesheetFieldError("taskId", "Selected task no longer exists."));
            return errors;
        }

        if (task.RequiresTicket)
        {
            if (input.TicketId is null || input.TicketId == Guid.Empty)
                errors.Add(new TimesheetFieldError("ticketId", $"Ticket is required for task '{task.Name}'."));
            else if (!await TicketExistsAsync(input.TicketId.Value, ct))
                errors.Add(new TimesheetFieldError("ticketId", "Selected ticket does not exist."));
        }
        else if (input.TicketId is not null && input.TicketId != Guid.Empty)
        {
            errors.Add(new TimesheetFieldError("ticketId", $"Task '{task.Name}' cannot be linked to a ticket."));
        }

        return errors;
    }

    private async Task<bool> TicketExistsAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM tickets WHERE id = @ticketId AND is_deleted = FALSE)",
            new { ticketId }, cancellationToken: ct));
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class TimesheetUserRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public bool Enabled { get; set; }
        public bool Manager { get; set; }
    }

    private sealed class MonthDayBreakdownRow
    {
        public DateTime EntryDate { get; set; }
        public Guid TaskId { get; set; }
        public string TaskName { get; set; } = "";
        public bool IsAbsence { get; set; }
        public int Minutes { get; set; }
    }
}
