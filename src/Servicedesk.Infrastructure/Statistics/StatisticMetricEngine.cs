using System.Globalization;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Statistics;

public sealed class StatisticMetricEngine : IStatisticMetricEngine
{
    private readonly NpgsqlDataSource _dataSource;

    public StatisticMetricEngine(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<StatisticTileData> ComputeAsync(
        StatisticTile tile, Guid viewerId, CancellationToken ct = default)
    {
        var (from, to, periodLabel) = ResolvePeriod(tile.Period);

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
            _ => Empty(tile, periodLabel),
        };
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
    /// human label. Uses the server's current UTC date as "today" — timezone
    /// refinement is a later setting; for whole-period windows it is a wash
    /// except within an hour of midnight.
    private static (DateOnly From, DateOnly To, string Label) ResolvePeriod(string period)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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

    private static double ToHours(long minutes) => Math.Round(minutes / 60.0, 2);

    private static StatisticTileData Empty(StatisticTile tile, string periodLabel) =>
        new(tile.Id, tile.MetricKey, tile.ChartType, "hours", periodLabel, 0,
            Array.Empty<StatisticDataPoint>(), DateTime.UtcNow);
}
