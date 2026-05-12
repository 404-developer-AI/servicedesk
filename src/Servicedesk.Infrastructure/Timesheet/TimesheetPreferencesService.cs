using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class TimesheetPreferencesService : ITimesheetPreferencesService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public TimesheetPreferencesService(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public async Task<TimesheetPreferences> GetEffectiveAsync(Guid userId, CancellationToken ct = default)
    {
        var globals = await ReadGlobalsAsync(ct);
        var row = await ReadOverrideRowAsync(userId, ct);

        var dayStart = row?.DayStartMinutes ?? globals.DayStartMinutes;
        var dayTarget = row?.TargetDayMinutes ?? globals.TargetMinutesPerDay;
        var weekTarget = row?.TargetWeekMinutes ?? globals.TargetMinutesPerWeek;
        var workDays = row?.WorkDays is null
            ? globals.WorkDays
            : ParseWorkDays(row.WorkDays);
        var maxAbsence = row?.MaxAbsenceDayMinutes ?? globals.MaxAbsenceMinutesPerDay;
        var officeStart = row?.OfficeStartMinutes ?? globals.OfficeStartMinutes;
        var officeEnd = row?.OfficeEndMinutes ?? globals.OfficeEndMinutes;

        return new TimesheetPreferences(
            dayStart, dayTarget, weekTarget, workDays, maxAbsence, officeStart, officeEnd);
    }

    public async Task<TimesheetOverride?> GetOverrideAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await ReadOverrideRowAsync(userId, ct);
        if (row is null) return null;
        IReadOnlyList<int>? workDays = row.WorkDays is null ? null : ParseWorkDays(row.WorkDays);
        return new TimesheetOverride(
            row.DayStartMinutes,
            row.TargetDayMinutes,
            row.TargetWeekMinutes,
            workDays,
            row.MaxAbsenceDayMinutes,
            row.OfficeStartMinutes,
            row.OfficeEndMinutes);
    }

    public async Task<UpdateOverrideResult> UpdateOverrideAsync(
        Guid userId, TimesheetOverrideInput input, CancellationToken ct = default)
    {
        var errors = new List<TimesheetFieldError>();
        if (input.DayStartMinutes is { } dsm && dsm is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("dayStartMinutes", "Day start must be between 00:00 and 24:00."));
        if (input.TargetMinutesPerDay is { } tdm && tdm is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("targetMinutesPerDay", "Daily target must be between 0 and 1440 minutes."));
        if (input.TargetMinutesPerWeek is { } twm && twm is < 0 or > 10080)
            errors.Add(new TimesheetFieldError("targetMinutesPerWeek", "Weekly target must be between 0 and 10080 minutes."));
        if (input.MaxAbsenceMinutesPerDay is { } mam && mam is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("maxAbsenceMinutesPerDay", "Max absence per day must be between 0 and 1440 minutes."));
        if (input.OfficeStartMinutes is { } oss && oss is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("officeStartMinutes", "Office start must be between 00:00 and 24:00."));
        if (input.OfficeEndMinutes is { } oes && oes is < 0 or > 1440)
            errors.Add(new TimesheetFieldError("officeEndMinutes", "Office end must be between 00:00 and 24:00."));
        if (input.OfficeStartMinutes is { } oss2 && input.OfficeEndMinutes is { } oes2 && oes2 <= oss2)
            errors.Add(new TimesheetFieldError("officeEndMinutes", "Office end must be after office start."));
        string? workDaysCsv = null;
        if (input.WorkDays is not null)
        {
            var sorted = input.WorkDays
                .Where(d => d is >= 1 and <= 7)
                .Distinct()
                .OrderBy(d => d)
                .ToList();
            if (sorted.Count != input.WorkDays.Count(d => d is >= 1 and <= 7))
            {
                // Sorted+distinct removed duplicates silently — that's fine,
                // it's the canonical form. Mismatch is only relevant if
                // there were out-of-range values; we already filter those.
            }
            workDaysCsv = string.Join(",", sorted);
        }
        if (errors.Count > 0) return new UpdateOverrideResult.ValidationFailed(errors);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET
                timesheet_start_minutes              = @dayStart,
                timesheet_target_day_minutes         = @dayTarget,
                timesheet_target_week_minutes        = @weekTarget,
                timesheet_work_days                  = @workDays,
                timesheet_max_absence_day_minutes    = @maxAbsence,
                timesheet_office_start_minutes       = @officeStart,
                timesheet_office_end_minutes         = @officeEnd
            WHERE id = @userId
            """,
            new
            {
                userId,
                dayStart = input.DayStartMinutes,
                dayTarget = input.TargetMinutesPerDay,
                weekTarget = input.TargetMinutesPerWeek,
                workDays = workDaysCsv,
                maxAbsence = input.MaxAbsenceMinutesPerDay,
                officeStart = input.OfficeStartMinutes,
                officeEnd = input.OfficeEndMinutes,
            },
            cancellationToken: ct));
        if (rows == 0) return new UpdateOverrideResult.UserNotFound();

        var stored = await GetOverrideAsync(userId, ct);
        return new UpdateOverrideResult.Updated(
            stored ?? new TimesheetOverride(null, null, null, null, null, null, null));
    }

    private async Task<TimesheetPreferences> ReadGlobalsAsync(CancellationToken ct)
    {
        var dayStart = await _settings.GetAsync<int>(SettingKeys.Timesheet.DefaultDayStartMinutes, ct);
        var dayTarget = await _settings.GetAsync<int>(SettingKeys.Timesheet.DefaultTargetMinutesPerDay, ct);
        var weekTarget = await _settings.GetAsync<int>(SettingKeys.Timesheet.DefaultTargetMinutesPerWeek, ct);
        var workDaysCsv = await _settings.GetAsync<string>(SettingKeys.Timesheet.DefaultWorkDays, ct);
        var maxAbsence = await _settings.GetAsync<int>(SettingKeys.Timesheet.DefaultMaxAbsenceMinutesPerDay, ct);
        var officeStart = await _settings.GetAsync<int>(SettingKeys.Timesheet.DefaultOfficeStartMinutes, ct);
        var officeEnd = await _settings.GetAsync<int>(SettingKeys.Timesheet.DefaultOfficeEndMinutes, ct);
        return new TimesheetPreferences(
            ClampMinute(dayStart, 510),
            ClampMinute(dayTarget, 480),
            Math.Clamp(weekTarget, 0, 10080),
            ParseWorkDays(workDaysCsv),
            ClampMinute(maxAbsence, 30),
            ClampMinute(officeStart, 510),
            ClampMinute(officeEnd, 1020));
    }

    private async Task<OverrideRow?> ReadOverrideRowAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT timesheet_start_minutes              AS DayStartMinutes,
                   timesheet_target_day_minutes         AS TargetDayMinutes,
                   timesheet_target_week_minutes        AS TargetWeekMinutes,
                   timesheet_work_days                  AS WorkDays,
                   timesheet_max_absence_day_minutes    AS MaxAbsenceDayMinutes,
                   timesheet_office_start_minutes       AS OfficeStartMinutes,
                   timesheet_office_end_minutes         AS OfficeEndMinutes
            FROM users WHERE id = @userId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<OverrideRow>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    private static int ClampMinute(int value, int fallback) =>
        value is < 0 or > 1440 ? fallback : value;

    /// "1,2,3,4,5" → [1,2,3,4,5]. Empty / unparseable bits are dropped.
    /// Out-of-range numbers are filtered. Output is sorted + deduplicated.
    private static IReadOnlyList<int> ParseWorkDays(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var output = new SortedSet<int>();
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var n) && n is >= 1 and <= 7)
                output.Add(n);
        }
        return output.ToList();
    }

    /// Dapper + readonly record struct doesn't play (feedback memory:
    /// dapper_record_struct_null_bug). Read into a mutable class, then
    /// project. All four fields are nullable to mirror the DB columns.
    private sealed class OverrideRow
    {
        public int? DayStartMinutes { get; set; }
        public int? TargetDayMinutes { get; set; }
        public int? TargetWeekMinutes { get; set; }
        public string? WorkDays { get; set; }
        public int? MaxAbsenceDayMinutes { get; set; }
        public int? OfficeStartMinutes { get; set; }
        public int? OfficeEndMinutes { get; set; }
    }
}
