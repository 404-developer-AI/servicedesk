using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class TimesheetEntryService : ITimesheetEntryService
{
    private const string SelectFromJoin = """
        SELECT  e.id              AS Id,
                e.user_id         AS UserId,
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
        LEFT JOIN tickets      k ON k.id = e.ticket_id
        LEFT JOIN companies    c ON c.id = k.company_id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITimesheetTaskService _tasks;

    public TimesheetEntryService(NpgsqlDataSource dataSource, ITimesheetTaskService tasks)
    {
        _dataSource = dataSource;
        _tasks = tasks;
    }

    public async Task<IReadOnlyList<TimesheetEntryRow>> ListByDateAsync(
        Guid userId, DateOnly date, CancellationToken ct = default)
    {
        var sql = SelectFromJoin + """

            WHERE e.user_id = @userId
              AND e.entry_date = @date
            ORDER BY e.start_minutes ASC, e.created_utc ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimesheetEntryRow>(new CommandDefinition(
            sql, new { userId, date = date.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<TimesheetEntryRow?> GetForUserAsync(
        Guid id, Guid userId, CancellationToken ct = default)
    {
        var sql = SelectFromJoin + """

            WHERE e.id = @id AND e.user_id = @userId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TimesheetEntryRow>(
            new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }

    public async Task<CreateTimesheetEntryResult> CreateAsync(
        Guid userId, TimesheetEntryInput input, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(input, ct);
        if (validation.Count > 0) return new CreateTimesheetEntryResult.ValidationFailed(validation);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO timesheet_entries (
                user_id, entry_date, start_minutes, end_minutes, minutes,
                task_id, ticket_id, description, created_by, updated_by)
            VALUES (
                @userId, @date, @start, @end, @minutes,
                @taskId, @ticketId, @description, @userId, @userId)
            RETURNING id
            """,
            new
            {
                userId,
                date = input.EntryDate.ToDateTime(TimeOnly.MinValue),
                start = input.StartMinutes,
                end = input.EndMinutes,
                minutes = input.EndMinutes - input.StartMinutes,
                taskId = input.TaskId,
                ticketId = input.TicketId,
                description = input.Description.Trim(),
            },
            cancellationToken: ct));

        var saved = await GetForUserAsync(id, userId, ct);
        return new CreateTimesheetEntryResult.Created(saved!);
    }

    public async Task<UpdateTimesheetEntryResult> UpdateAsync(
        Guid id, Guid userId, TimesheetEntryInput input, CancellationToken ct = default)
    {
        var existing = await GetForUserAsync(id, userId, ct);
        if (existing is null) return new UpdateTimesheetEntryResult.NotFound();

        var validation = await ValidateAsync(input, ct);
        if (validation.Count > 0) return new UpdateTimesheetEntryResult.ValidationFailed(validation);

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
                updated_by    = @userId,
                updated_utc   = now()
            WHERE id = @id AND user_id = @userId
            """,
            new
            {
                id,
                userId,
                date = input.EntryDate.ToDateTime(TimeOnly.MinValue),
                start = input.StartMinutes,
                end = input.EndMinutes,
                minutes = input.EndMinutes - input.StartMinutes,
                taskId = input.TaskId,
                ticketId = input.TicketId,
                description = input.Description.Trim(),
            },
            cancellationToken: ct));
        if (rows == 0) return new UpdateTimesheetEntryResult.NotFound();

        var saved = await GetForUserAsync(id, userId, ct);
        return new UpdateTimesheetEntryResult.Updated(saved!);
    }

    public async Task<bool> DeleteOwnAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM timesheet_entries WHERE id = @id AND user_id = @userId",
            new { id, userId },
            cancellationToken: ct));
        return rows > 0;
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
            else if (!await TicketExistsAsync(input.TicketId.Value, ct))
            {
                errors.Add(new TimesheetFieldError("ticketId", "Selected ticket does not exist."));
            }
        }
        else if (input.TicketId is not null && input.TicketId != Guid.Empty)
        {
            // Task doesn't allow tickets — silently clear instead of
            // rejecting, since the user-facing UI already disables the
            // ticket field. Belt-and-suspenders against a stale form.
            // Implemented at the call sites in CreateAsync/UpdateAsync by
            // passing input as-is; we surface a soft error here to be
            // defensive.
            errors.Add(new TimesheetFieldError("ticketId", $"Task '{task.Name}' cannot be linked to a ticket."));
        }

        return errors;
    }

    private async Task<bool> TicketExistsAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM tickets WHERE id = @ticketId AND is_deleted = FALSE)",
            new { ticketId },
            cancellationToken: ct));
        return exists;
    }
}
