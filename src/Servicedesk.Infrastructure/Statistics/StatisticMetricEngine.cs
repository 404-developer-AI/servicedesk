using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Statistics;

public sealed class StatisticMetricEngine : IStatisticMetricEngine
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public StatisticMetricEngine(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public async Task<StatisticTileData> ComputeAsync(
        StatisticTile tile, Guid viewerId, CancellationToken ct = default)
    {
        // Period boundaries are resolved in the configured application
        // timezone (Settings → General → App.TimeZone) so "today" / "this
        // week" line up with the local working day, not UTC.
        var tzId = await _settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, ResolveTimeZone(tzId)));
        var (from, to, periodLabel) = ResolvePeriod(tile.Period, today);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var targets = await ResolveTargetsAsync(connection, tile, viewerId, ct);

        // No targets (e.g. scope='user' whose technician was deleted) → an
        // honest empty tile rather than an error.
        if (targets.Length == 0)
        {
            return Empty(tile, periodLabel);
        }

        return tile.MetricKey switch
        {
            StatisticMetricKeys.WorkedHours =>
                await ComputeWorkedHoursAsync(connection, tile, targets, from, to, periodLabel, ct),
            StatisticMetricKeys.BillableHours =>
                await ComputeBillableHoursAsync(connection, tile, targets, from, to, periodLabel,
                    await _settings.GetAsync<decimal>(SettingKeys.Timesheet.HourlyRate, ct), ct),
            _ => Empty(tile, periodLabel),
        };
    }

    // ---- billable vs non-billable ----------------------------------------

    private static async Task<StatisticTileData> ComputeBillableHoursAsync(
        NpgsqlConnection connection,
        StatisticTile tile,
        Guid[] targets,
        DateOnly from,
        DateOnly to,
        string periodLabel,
        decimal hourlyRate,
        CancellationToken ct)
    {
        var args = new
        {
            ids = targets,
            from = from.ToDateTime(TimeOnly.MinValue),
            to = to.ToDateTime(TimeOnly.MinValue),
            // Adsolut sales receipts in this install carry the invoiced VALUE
            // (total_excl_vat) but not per-performance invoiced minutes, so we
            // express "billed" as value ÷ hourly rate — the same basis as the
            // Adsolut timesheet tab. Rate <= 0 (unset) → nothing billable.
            rate = hourlyRate > 0 ? (decimal?)hourlyRate : null,
        };

        // Worked minutes per target in the period.
        var worked = (await connection.QueryAsync<(Guid UserId, long Minutes)>(new CommandDefinition(
            """
            SELECT e.user_id AS UserId, COALESCE(SUM(e.minutes), 0)::bigint AS Minutes
            FROM timesheet_entries e
            WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
            GROUP BY e.user_id
            """,
            args, cancellationToken: ct))).ToDictionary(r => r.UserId, r => r.Minutes);

        // Billable minutes per target: the invoiced value on each ticket
        // (Σ total_excl_vat) converted to minutes via the hourly rate, then
        // pro-rated by the target's share of the minutes logged on that ticket
        // (within the period) and capped at what they worked. Worked side is
        // bounded on entry_date, the invoiced side on sales_receipt_date.
        var billable = (await connection.QueryAsync<(Guid UserId, double Minutes)>(new CommandDefinition(
            """
            WITH tpm AS (
                SELECT e.ticket_id, e.user_id, SUM(e.minutes)::numeric AS m
                FROM timesheet_entries e
                WHERE e.ticket_id IS NOT NULL AND e.entry_date BETWEEN @from AND @to
                GROUP BY e.ticket_id, e.user_id
            ),
            tt AS (
                SELECT ticket_id, SUM(m) AS total_m FROM tpm GROUP BY ticket_id
            ),
            inv AS (
                SELECT tk.id AS ticket_id,
                       CASE WHEN @rate IS NOT NULL AND @rate > 0
                            THEN COALESCE(SUM(r.total_excl_vat), 0) * 60.0 / @rate
                            ELSE 0 END::numeric AS inv_m
                FROM tickets tk
                JOIN adsolut_sales_receipts r ON r.ticket_number = tk.number
                WHERE r.sales_receipt_date::date BETWEEN @from::date AND @to::date
                GROUP BY tk.id
            )
            SELECT tpm.user_id AS UserId,
                   SUM(LEAST(
                       tpm.m,
                       CASE WHEN tt.total_m > 0
                            THEN COALESCE(inv.inv_m, 0) * tpm.m / tt.total_m
                            ELSE 0 END))::double precision AS Minutes
            FROM tpm
            JOIN tt ON tt.ticket_id = tpm.ticket_id
            LEFT JOIN inv ON inv.ticket_id = tpm.ticket_id
            WHERE tpm.user_id = ANY(@ids)
            GROUP BY tpm.user_id
            """,
            args, cancellationToken: ct))).ToDictionary(r => r.UserId, r => r.Minutes);

        var emails = await LoadEmailsAsync(connection, targets, ct);
        var single = tile.Scope != StatisticScopes.Team;

        var points = new List<StatisticDataPoint>();
        double totalBillable = 0;
        foreach (var uid in targets)
        {
            var workedMin = worked.TryGetValue(uid, out var w) ? w : 0;
            if (workedMin <= 0) continue;
            var billableMin = billable.TryGetValue(uid, out var b) ? b : 0;
            if (billableMin < 0) billableMin = 0;
            var nonBillableMin = Math.Max(workedMin - billableMin, 0);
            totalBillable += billableMin;

            var label = single
                ? (tile.Scope == StatisticScopes.ViewerSelf ? "You" : emails.GetValueOrDefault(uid) ?? "Technician")
                : emails.GetValueOrDefault(uid) ?? "Technician";
            points.Add(new StatisticDataPoint(
                label,
                ToHours((long)Math.Round(billableMin)),
                ToHours((long)Math.Round(nonBillableMin))));
        }

        // Team view sorts by billable desc for a leaderboard feel.
        if (!single) points = points.OrderByDescending(p => p.Value).ToList();

        return new StatisticTileData(
            TileId: tile.Id,
            MetricKey: tile.MetricKey,
            ChartType: tile.ChartType,
            Unit: "hours",
            PeriodLabel: periodLabel,
            Total: ToHours((long)Math.Round(totalBillable)),
            Points: points,
            GeneratedUtc: DateTime.UtcNow,
            SeriesLabels: new[] { "Billable", "Non-billable" });
    }

    private static async Task<Dictionary<Guid, string>> LoadEmailsAsync(
        NpgsqlConnection connection, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return new Dictionary<Guid, string>();
        var rows = await connection.QueryAsync<(Guid Id, string Email)>(new CommandDefinition(
            "SELECT id AS Id, email AS Email FROM users WHERE id = ANY(@ids)",
            new { ids }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Email);
    }

    // ---- worked hours -----------------------------------------------------

    private static async Task<StatisticTileData> ComputeWorkedHoursAsync(
        NpgsqlConnection connection,
        StatisticTile tile,
        Guid[] targets,
        DateOnly from,
        DateOnly to,
        string periodLabel,
        CancellationToken ct)
    {
        // This Dapper version has no DateOnly parameter handler — pass the
        // range as DateTime (midnight), matching the timesheet services. The
        // entry_date column is DATE, so the 00:00 time on @to still includes
        // that whole day.
        var args = new
        {
            ids = targets,
            from = from.ToDateTime(TimeOnly.MinValue),
            to = to.ToDateTime(TimeOnly.MinValue),
        };

        // Total is always computed (KPI uses it; bar uses it as the sum line).
        var totalMinutes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COALESCE(SUM(e.minutes), 0)::bigint
            FROM timesheet_entries e
            WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
            """,
            args, cancellationToken: ct));

        var points = new List<StatisticDataPoint>();

        if (string.Equals(tile.Grouping, StatisticGroupings.Task, StringComparison.Ordinal))
        {
            var rows = await connection.QueryAsync<(string Label, long Minutes)>(new CommandDefinition(
                """
                SELECT t.name AS Label, COALESCE(SUM(e.minutes), 0)::bigint AS Minutes
                FROM timesheet_entries e
                JOIN timesheet_tasks t ON t.id = e.task_id
                WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
                GROUP BY t.name
                ORDER BY Minutes DESC, t.name ASC
                """,
                args, cancellationToken: ct));
            points.AddRange(rows.Select(r => new StatisticDataPoint(r.Label, ToHours(r.Minutes))));
        }
        else if (string.Equals(tile.Grouping, StatisticGroupings.Time, StringComparison.Ordinal))
        {
            points.AddRange(await ComputeTimeBucketsAsync(connection, tile.Period, args, ct));
        }
        else
        {
            // grouping = none → a single bucket carrying the period total.
            points.Add(new StatisticDataPoint(periodLabel, ToHours(totalMinutes)));
        }

        return new StatisticTileData(
            TileId: tile.Id,
            MetricKey: tile.MetricKey,
            ChartType: tile.ChartType,
            Unit: "hours",
            PeriodLabel: periodLabel,
            Total: ToHours(totalMinutes),
            Points: points,
            GeneratedUtc: DateTime.UtcNow);
    }

    private static async Task<IReadOnlyList<StatisticDataPoint>> ComputeTimeBucketsAsync(
        NpgsqlConnection connection, string period, object args, CancellationToken ct)
    {
        if (string.Equals(period, StatisticPeriods.Day, StringComparison.Ordinal))
        {
            // One bar per hour of the day (by the entry's start hour).
            var rows = await connection.QueryAsync<(int Hour, long Minutes)>(new CommandDefinition(
                """
                SELECT (e.start_minutes / 60) AS Hour, COALESCE(SUM(e.minutes), 0)::bigint AS Minutes
                FROM timesheet_entries e
                WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
                GROUP BY (e.start_minutes / 60)
                ORDER BY Hour ASC
                """,
                args, cancellationToken: ct));
            return rows
                .Select(r => new StatisticDataPoint($"{r.Hour:D2}:00", ToHours(r.Minutes)))
                .ToList();
        }

        if (string.Equals(period, StatisticPeriods.Year, StringComparison.Ordinal))
        {
            // One bar per month.
            var rows = await connection.QueryAsync<(DateTime Month, long Minutes)>(new CommandDefinition(
                """
                SELECT date_trunc('month', e.entry_date)::date AS Month, COALESCE(SUM(e.minutes), 0)::bigint AS Minutes
                FROM timesheet_entries e
                WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
                GROUP BY date_trunc('month', e.entry_date)
                ORDER BY Month ASC
                """,
                args, cancellationToken: ct));
            return rows
                .Select(r => new StatisticDataPoint(
                    r.Month.ToString("MMM", CultureInfo.InvariantCulture), ToHours(r.Minutes)))
                .ToList();
        }

        // week / month → one bar per day.
        var dayRows = await connection.QueryAsync<(DateTime Day, long Minutes)>(new CommandDefinition(
            """
            SELECT e.entry_date AS Day, COALESCE(SUM(e.minutes), 0)::bigint AS Minutes
            FROM timesheet_entries e
            WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
            GROUP BY e.entry_date
            ORDER BY e.entry_date ASC
            """,
            args, cancellationToken: ct));
        var label = string.Equals(period, StatisticPeriods.Week, StringComparison.Ordinal)
            ? (Func<DateTime, string>)(d => d.ToString("ddd", CultureInfo.InvariantCulture))
            : d => d.Day.ToString(CultureInfo.InvariantCulture);
        return dayRows.Select(r => new StatisticDataPoint(label(r.Day), ToHours(r.Minutes))).ToList();
    }

    // ---- scope + period ---------------------------------------------------

    private static async Task<Guid[]> ResolveTargetsAsync(
        NpgsqlConnection connection, StatisticTile tile, Guid viewerId, CancellationToken ct)
    {
        switch (tile.Scope)
        {
            case StatisticScopes.ViewerSelf:
                return new[] { viewerId };

            case StatisticScopes.User:
                return tile.ScopeUserId is { } id ? new[] { id } : Array.Empty<Guid>();

            case StatisticScopes.Team:
                var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM users WHERE role_name IN ('Agent','Admin') AND is_active = TRUE",
                    cancellationToken: ct));
                return rows.ToArray();

            default:
                return Array.Empty<Guid>();
        }
    }

    /// Resolves a period token into an inclusive [from, to] date range and a
    /// human label, relative to <paramref name="today"/> (already converted to
    /// the configured application timezone by the caller).
    private static (DateOnly From, DateOnly To, string Label) ResolvePeriod(string period, DateOnly today)
    {
        switch (period)
        {
            case StatisticPeriods.Day:
                return (today, today, $"Today — {today:dd MMM yyyy}");

            case StatisticPeriods.Week:
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                var sunday = monday.AddDays(6);
                return (monday, sunday, $"This week — {monday:dd MMM}–{sunday:dd MMM}");

            case StatisticPeriods.Year:
                var jan1 = new DateOnly(today.Year, 1, 1);
                var dec31 = new DateOnly(today.Year, 12, 31);
                return (jan1, dec31, $"This year — {today.Year}");

            case StatisticPeriods.Month:
            default:
                var first = new DateOnly(today.Year, today.Month, 1);
                var last = first.AddMonths(1).AddDays(-1);
                return (first, last, $"This month — {first:MMM yyyy}");
        }
    }

    /// Resolves the configured IANA timezone id, falling back to the server's
    /// local zone when unset/invalid (mirrors the helper used by the SLA +
    /// integrations code so all surfaces agree on the local day).
    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* Invalid IANA id — fall through. */ }
        }
        return TimeZoneInfo.Local;
    }

    private static double ToHours(long minutes) => Math.Round(minutes / 60.0, 2);

    private static StatisticTileData Empty(StatisticTile tile, string periodLabel) =>
        new(tile.Id, tile.MetricKey, tile.ChartType, "hours", periodLabel, 0,
            Array.Empty<StatisticDataPoint>(), DateTime.UtcNow);
}
