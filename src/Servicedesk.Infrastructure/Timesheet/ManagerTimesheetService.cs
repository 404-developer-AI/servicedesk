using System.Text;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class ManagerTimesheetService : IManagerTimesheetService
{
    /// Same projection as the own-row service, plus the agent's email so
    /// the manager grid can show "who" on every row.
    private const string SelectColumns = """
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
        """;

    /// The shared FROM + joins. Both the page query and the count/sum query
    /// build on this so a search predicate touching c/k/u resolves the same
    /// way in either.
    private const string FromJoins = """
        FROM timesheet_entries e
        JOIN timesheet_tasks   t ON t.id = e.task_id
        JOIN users             u ON u.id = e.user_id
        LEFT JOIN tickets      k ON k.id = e.ticket_id
        LEFT JOIN companies    c ON c.id = k.company_id
        """;

    private const string SelectFromJoin = SelectColumns + "\n" + FromJoins;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITimesheetTaskService _tasks;
    private readonly ISettingsService _settings;

    public ManagerTimesheetService(
        NpgsqlDataSource dataSource, ITimesheetTaskService tasks, ISettingsService settings)
    {
        _dataSource = dataSource;
        _tasks = tasks;
        _settings = settings;
    }

    /// Page sizes the UI offers. Any other value is clamped into this range
    /// so a hand-crafted request can't ask for an unbounded page.
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 10;

    public async Task<ManagerEntryPage> ListAsync(
        ManagerEntryFilter filter, CancellationToken ct = default)
    {
        var pageSize = filter.PageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => filter.PageSize,
        };
        var page = filter.Page < 1 ? 1 : filter.Page;

        // Build the shared predicate once. No date bound = every day; the
        // pager (LIMIT/OFFSET) is what keeps an open query bounded.
        var where = new StringBuilder("WHERE 1 = 1\n");
        var p = new DynamicParameters();

        var from = filter.From;
        var to = filter.To;
        if (from is not null && to is not null && to < from) (from, to) = (to, from);
        if (from is not null)
        {
            where.AppendLine("  AND e.entry_date >= @from");
            p.Add("from", from.Value.ToDateTime(TimeOnly.MinValue));
        }
        if (to is not null)
        {
            where.AppendLine("  AND e.entry_date <= @to");
            p.Add("to", to.Value.ToDateTime(TimeOnly.MinValue));
        }

        if (filter.UserId is { } uid && uid != Guid.Empty)
        {
            where.AppendLine("  AND e.user_id = @userId");
            p.Add("userId", uid);
        }
        if (filter.TicketId is { } tid && tid != Guid.Empty)
        {
            where.AppendLine("  AND e.ticket_id = @ticketId");
            p.Add("ticketId", tid);
        }
        if (filter.TaskId is { } taskId && taskId != Guid.Empty)
        {
            where.AppendLine("  AND e.task_id = @taskId");
            p.Add("taskId", taskId);
        }

        var trimmedSearch = (filter.Search ?? string.Empty).Trim();
        if (trimmedSearch.Length > 0)
        {
            // Free-text on company name, contact name (via tickets.requester_contact_id),
            // ticket subject, and the entry's own description. ILIKE is fine
            // here because the dataset per manager session is small.
            where.AppendLine("""
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

        var whereSql = where.ToString();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Totals across the whole filtered set drive the pager and footer sum.
        var totals = await conn.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            "SELECT COUNT(*)::int AS Total, COALESCE(SUM(e.minutes), 0)::int AS TotalMinutes\n"
            + FromJoins + "\n" + whereSql,
            p, cancellationToken: ct));

        var dataSql = SelectColumns + "\n" + FromJoins + "\n" + whereSql
            + "ORDER BY e.entry_date DESC, e.start_minutes ASC, u.email ASC\n"
            + "LIMIT @pageSize OFFSET @offset";
        p.Add("pageSize", pageSize);
        p.Add("offset", (page - 1) * pageSize);

        var rows = await conn.QueryAsync<TimesheetEntryRow>(
            new CommandDefinition(dataSql, p, cancellationToken: ct));

        return new ManagerEntryPage(rows.ToList(), totals.Total, totals.TotalMinutes, page, pageSize);
    }

    private sealed class TotalsRow
    {
        public int Total { get; set; }
        public int TotalMinutes { get; set; }
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

        // First/last clock per day: MIN(start)/MAX(end) over every entry —
        // absence entries included by design (the columns answer "when was
        // the first/last line of the day typed in", not "when did work
        // start"). Separate query because the breakdown query groups by
        // task and cannot carry a per-day extreme.
        const string clockSql = """
            SELECT  e.entry_date            AS EntryDate,
                    MIN(e.start_minutes)    AS FirstClockMinutes,
                    MAX(e.end_minutes)      AS LastClockMinutes
            FROM timesheet_entries e
            WHERE e.user_id = @userId
              AND e.entry_date BETWEEN @from AND @to
            GROUP BY e.entry_date
            """;
        var clockRows = await conn.QueryAsync<DayClockRow>(
            new CommandDefinition(clockSql,
                new
                {
                    userId,
                    from = first.ToDateTime(TimeOnly.MinValue),
                    to = last.ToDateTime(TimeOnly.MinValue),
                },
                cancellationToken: ct));
        var clockByDate = clockRows.ToDictionary(r => DateOnly.FromDateTime(r.EntryDate));

        // First login per day, from the audit log (password + M365 login
        // events; the password event fires at the password step, before
        // TOTP, which is the true "first login" moment). Audit stamps are
        // UTC — the local month window and the per-day bucketing both use
        // the configured app timezone so a 23:30 UTC login lands on the
        // right local day.
        var tzId = await _settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
        var tz = ResolveTimeZone(tzId);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(first.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(last.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);

        const string loginSql = """
            SELECT  utc         AS Utc,
                    event_type  AS EventType
            FROM audit_log
            WHERE event_type = ANY(@types)
              AND target = @target
              AND utc >= @fromUtc AND utc < @toUtc
            """;
        var loginRows = await conn.QueryAsync<LoginStampRow>(
            new CommandDefinition(loginSql,
                new
                {
                    types = new[] { AuthEventTypes.LoginSuccess, AuthEventTypes.MicrosoftLoginSuccess },
                    target = userId.ToString(),
                    fromUtc,
                    toUtc,
                },
                cancellationToken: ct));
        var loginByDate = BucketFirstLogins(
            loginRows.Select(r => (r.Utc, r.EventType)), tz);

        var days = new List<MonthDayRollup>(DateTime.DaysInMonth(year, month));
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var clock = clockByDate.TryGetValue(d, out var c) ? c : null;
            var login = loginByDate.TryGetValue(d, out var l) ? l : ((int, string)?)null;
            if (byDate.TryGetValue(d, out var list))
            {
                var work = list.Where(r => !r.IsAbsence).Sum(r => r.Minutes);
                var absence = list.Where(r => r.IsAbsence).Sum(r => r.Minutes);
                var breakdown = list
                    .Select(r => new MonthDayBreakdown(r.TaskId, r.TaskName, r.IsAbsence, r.Minutes))
                    .ToList();
                days.Add(new MonthDayRollup(d, work, absence, list.Count, breakdown,
                    login?.Item1, login?.Item2, clock?.FirstClockMinutes, clock?.LastClockMinutes));
            }
            else
            {
                days.Add(new MonthDayRollup(d, 0, 0, 0, Array.Empty<MonthDayBreakdown>(),
                    login?.Item1, login?.Item2, clock?.FirstClockMinutes, clock?.LastClockMinutes));
            }
        }

        return new MonthRollup(userId, email, year, month, days);
    }

    /// Buckets successful-login audit stamps (UTC) into local days and
    /// keeps the earliest per day. Returns minutes-since-midnight in the
    /// given timezone plus the login kind ("microsoft" for the M365 OIDC
    /// event, "password" otherwise). Public + static so the day-boundary
    /// behaviour is unit-testable without a database.
    public static Dictionary<DateOnly, (int Minutes, string Kind)> BucketFirstLogins(
        IEnumerable<(DateTime Utc, string EventType)> logins, TimeZoneInfo tz)
    {
        var result = new Dictionary<DateOnly, (int Minutes, string Kind)>();
        foreach (var (utc, eventType) in logins)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
            var day = DateOnly.FromDateTime(local);
            var minutes = local.Hour * 60 + local.Minute;
            if (!result.TryGetValue(day, out var existing) || minutes < existing.Minutes)
            {
                var kind = eventType == AuthEventTypes.MicrosoftLoginSuccess ? "microsoft" : "password";
                result[day] = (minutes, kind);
            }
        }
        return result;
    }

    /// Mirrors the resolver used by statistics/SLA so every surface agrees
    /// on what "the local day" means: configured IANA id, else server-local.
    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* Invalid IANA id — fall through. */ }
        }
        return TimeZoneInfo.Local;
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
            {
                errors.Add(new TimesheetFieldError("ticketId", $"Ticket is required for task '{task.Name}'."));
            }
            else
            {
                var check = await CheckTicketAsync(input.TicketId.Value, ct);
                if (!check.Exists)
                {
                    errors.Add(new TimesheetFieldError("ticketId", "Selected ticket does not exist."));
                }
                else if (check.IsMerged)
                {
                    errors.Add(new TimesheetFieldError(
                        "ticketId",
                        check.MergedIntoNumber > 0
                            ? $"Ticket has been merged into #{check.MergedIntoNumber}. Log time on the surviving ticket instead."
                            : "Ticket has been merged. Log time on the surviving ticket instead."));
                }
            }
        }
        else if (input.TicketId is not null && input.TicketId != Guid.Empty)
        {
            errors.Add(new TimesheetFieldError("ticketId", $"Task '{task.Name}' cannot be linked to a ticket."));
        }

        return errors;
    }

    private async Task<TicketCheck> CheckTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TicketCheckRow>(new CommandDefinition(
            """
            SELECT t.merged_into_ticket_id AS MergedIntoTicketId,
                   COALESCE(m.number, 0)    AS MergedIntoNumber
              FROM tickets t
              LEFT JOIN tickets m ON m.id = t.merged_into_ticket_id
             WHERE t.id = @ticketId AND t.is_deleted = FALSE
            """,
            new { ticketId }, cancellationToken: ct));
        if (row is null) return new TicketCheck(false, false, 0);
        return new TicketCheck(true, row.MergedIntoTicketId is not null, row.MergedIntoNumber);
    }

    private readonly record struct TicketCheck(bool Exists, bool IsMerged, long MergedIntoNumber);

    private sealed class TicketCheckRow
    {
        public Guid? MergedIntoTicketId { get; set; }
        public long MergedIntoNumber { get; set; }
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

    private sealed class DayClockRow
    {
        public DateTime EntryDate { get; set; }
        public int FirstClockMinutes { get; set; }
        public int LastClockMinutes { get; set; }
    }

    private sealed class LoginStampRow
    {
        public DateTime Utc { get; set; }
        public string EventType { get; set; } = "";
    }
}
