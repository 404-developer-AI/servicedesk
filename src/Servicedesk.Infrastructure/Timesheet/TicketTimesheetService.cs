using System.Globalization;
using System.Net;
using System.Text;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class TicketTimesheetService : ITicketTimesheetService
{
    /// Same projection as the manager service, minus the company/contact
    /// joins we don't need here. We DO carry user_email because the panel
    /// must show "who" on each row.
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
    private readonly ISettingsService _settings;

    public TicketTimesheetService(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public async Task<IReadOnlyList<TimesheetEntryRow>> ListByTicketAsync(
        Guid ticketId, CancellationToken ct = default)
    {
        var sql = SelectFromJoin + """

            WHERE e.ticket_id = @ticketId
            ORDER BY e.entry_date ASC, e.start_minutes ASC, u.email ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimesheetEntryRow>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<string> BuildReplyHtmlAsync(Guid ticketId, CancellationToken ct = default)
    {
        var rows = await ListByTicketAsync(ticketId, ct);

        var header = await _settings.GetAsync<string>(SettingKeys.Timesheet.ReplyHeaderHtml, ct);
        var rowTpl = await _settings.GetAsync<string>(SettingKeys.Timesheet.ReplyRowHtml, ct);
        var footer = await _settings.GetAsync<string>(SettingKeys.Timesheet.ReplyFooterHtml, ct);

        var sb = new StringBuilder();
        sb.Append(header);
        foreach (var r in rows)
        {
            sb.Append(RenderRow(rowTpl, r));
        }
        sb.Append(RenderFooter(footer, rows));
        return sb.ToString();
    }

    private static string RenderRow(string template, TimesheetEntryRow r)
    {
        var date = r.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return template
            .Replace("{{date}}", Esc(date))
            .Replace("{{start}}", Esc(FormatHHMM(r.StartMinutes)))
            .Replace("{{end}}", Esc(FormatHHMM(r.EndMinutes)))
            .Replace("{{duration}}", Esc(FormatDuration(r.Minutes)))
            .Replace("{{minutes}}", r.Minutes.ToString(CultureInfo.InvariantCulture))
            .Replace("{{description}}", Esc(r.Description))
            .Replace("{{agent}}", Esc(r.UserEmail))
            .Replace("{{task}}", Esc(r.TaskName));
    }

    private static string RenderFooter(string template, IReadOnlyList<TimesheetEntryRow> rows)
    {
        var totalMinutes = rows.Sum(r => r.Minutes);
        return template
            .Replace("{{total_duration}}", Esc(FormatDuration(totalMinutes)))
            .Replace("{{total_minutes}}", totalMinutes.ToString(CultureInfo.InvariantCulture))
            .Replace("{{total_hours}}", (totalMinutes / 60.0).ToString("0.##", CultureInfo.InvariantCulture))
            .Replace("{{count}}", rows.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private static string FormatHHMM(int minutes)
    {
        var safe = Math.Max(0, Math.Min(1440, minutes));
        var hh = safe / 60;
        var mm = safe % 60;
        return $"{hh:D2}:{mm:D2}";
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes <= 0) return "0m";
        var hh = minutes / 60;
        var mm = minutes % 60;
        if (hh == 0) return $"{mm}m";
        if (mm == 0) return $"{hh}h";
        return $"{hh}h {mm}m";
    }
}
