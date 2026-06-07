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
        StatisticTile tile, Guid viewerId, int periodOffset = 0, CancellationToken ct = default)
    {
        // Period boundaries are resolved in the configured application
        // timezone (Settings → General → App.TimeZone) so "today" / "this
        // week" line up with the local working day, not UTC. periodOffset
        // shifts the window by whole units for prev/next navigation.
        var tzId = await _settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
        var tz = ResolveTimeZone(tzId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz));
        var (from, to, periodLabel) = ResolvePeriod(tile.Period, today, periodOffset);
        // UTC instants for the same window — used by metrics that filter on a
        // timestamptz column (ticket_events.created_utc), unlike the DATE-typed
        // timesheet columns which compare on the local date directly.
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), tz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

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
            StatisticMetricKeys.TicketsResolved =>
                await ComputeTicketCountAsync(connection, tile, targets, fromUtc, toUtc, periodLabel,
                    SettingKeys.Timesheet.ResolvedTabStatusIds, ct),
            StatisticMetricKeys.TicketsCwi =>
                await ComputeTicketCountAsync(connection, tile, targets, fromUtc, toUtc, periodLabel,
                    SettingKeys.Timesheet.CwiTabStatusIds, ct),
            StatisticMetricKeys.HoursByStatusGroup =>
                await ComputeHoursByStatusGroupAsync(connection, tile, targets, from, to, periodLabel, ct),
            _ => Empty(tile, periodLabel),
        };
    }

    // ---- hours by status group (Resolved / CWI / QFI / WFQ) --------------

    private async Task<StatisticTileData> ComputeHoursByStatusGroupAsync(
        NpgsqlConnection connection,
        StatisticTile tile,
        Guid[] targets,
        DateOnly from,
        DateOnly to,
        string periodLabel,
        CancellationToken ct)
    {
        // Each group is a configurable status-id set. Resolved/CWI reuse the
        // back-office sets; QFI/WFQ are statistics-only. A group with no
        // configured statuses is omitted from the chart entirely.
        var groups = new (string Label, Guid[] Ids)[]
        {
            ("Resolved", ParseStatusGuids(await _settings.GetAsync<string>(SettingKeys.Timesheet.ResolvedTabStatusIds, ct))),
            ("CWI",      ParseStatusGuids(await _settings.GetAsync<string>(SettingKeys.Timesheet.CwiTabStatusIds, ct))),
            ("QFI",      ParseStatusGuids(await _settings.GetAsync<string>(SettingKeys.Statistics.QfiStatusIds, ct))),
            ("WFQ",      ParseStatusGuids(await _settings.GetAsync<string>(SettingKeys.Statistics.WfqStatusIds, ct))),
        };

        // One pass over the period's entries, bucketed by the ticket's CURRENT
        // status into each group via FILTER. Hours logged on tickets whose
        // status is in the group's set, summed across the target users.
        var row = await connection.QueryFirstOrDefaultAsync<StatusGroupRow>(new CommandDefinition(
            """
            SELECT
                COALESCE(SUM(e.minutes) FILTER (WHERE t.status_id = ANY(@resolved)), 0)::bigint AS ResolvedM,
                COALESCE(SUM(e.minutes) FILTER (WHERE t.status_id = ANY(@cwi)),      0)::bigint AS CwiM,
                COALESCE(SUM(e.minutes) FILTER (WHERE t.status_id = ANY(@qfi)),      0)::bigint AS QfiM,
                COALESCE(SUM(e.minutes) FILTER (WHERE t.status_id = ANY(@wfq)),      0)::bigint AS WfqM
            FROM timesheet_entries e
            JOIN tickets t ON t.id = e.ticket_id
            WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
            """,
            new
            {
                ids = targets,
                from = from.ToDateTime(TimeOnly.MinValue),
                to = to.ToDateTime(TimeOnly.MinValue),
                resolved = groups[0].Ids,
                cwi = groups[1].Ids,
                qfi = groups[2].Ids,
                wfq = groups[3].Ids,
            }, cancellationToken: ct)) ?? new StatusGroupRow();

        var minutesByGroup = new[] { row.ResolvedM, row.CwiM, row.QfiM, row.WfqM };
        var points = new List<StatisticDataPoint>();
        long totalMin = 0;
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i].Ids.Length == 0) continue; // group not configured → omit
            totalMin += minutesByGroup[i];
            points.Add(new StatisticDataPoint(groups[i].Label, ToHours(minutesByGroup[i])));
        }

        return new StatisticTileData(
            TileId: tile.Id,
            MetricKey: tile.MetricKey,
            ChartType: tile.ChartType,
            Unit: "hours",
            PeriodLabel: periodLabel,
            Total: ToHours(totalMin),
            Points: points,
            GeneratedUtc: DateTime.UtcNow);
    }

    private sealed class StatusGroupRow
    {
        public long ResolvedM { get; set; }
        public long CwiM { get; set; }
        public long QfiM { get; set; }
        public long WfqM { get; set; }
    }

    private static Guid[] ParseStatusGuids(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<Guid>();
        var set = new HashSet<Guid>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var g)) set.Add(g);
        }
        return set.ToArray();
    }

    // ---- ticket counts (resolved / CWI) ----------------------------------

    private async Task<StatisticTileData> ComputeTicketCountAsync(
        NpgsqlConnection connection,
        StatisticTile tile,
        Guid[] targets,
        DateTime fromUtc,
        DateTime toUtc,
        string periodLabel,
        string statusCsvSettingKey,
        CancellationToken ct)
    {
        var statusIds = ParseStatusIds(await _settings.GetAsync<string>(statusCsvSettingKey, ct));
        if (statusIds.Length == 0)
        {
            return Empty(tile, periodLabel, "tickets");
        }

        // Credit = the author of the StatusChange that moved the ticket INTO a
        // status in the configured set, counted in the period it happened.
        // metadata->>'to' holds the destination status id as text (same form
        // as status_id::text used by the back-office tabs). Distinct tickets
        // per author so a reopen+re-resolve in the period counts once.
        var counts = (await connection.QueryAsync<(Guid UserId, long Cnt)>(new CommandDefinition(
            """
            SELECT ev.author_user_id AS UserId, COUNT(DISTINCT ev.ticket_id)::bigint AS Cnt
            FROM ticket_events ev
            WHERE ev.event_type = 'StatusChange'
              AND ev.author_user_id = ANY(@ids)
              AND ev.metadata->>'to' = ANY(@statusIds)
              AND ev.created_utc >= @fromUtc AND ev.created_utc < @toUtc
            GROUP BY ev.author_user_id
            """,
            new { ids = targets, statusIds, fromUtc, toUtc }, cancellationToken: ct)))
            .ToDictionary(r => r.UserId, r => r.Cnt);

        var single = IsSingleScope(tile.Scope);
        var points = new List<StatisticDataPoint>();
        long total = 0;

        if (single)
        {
            var uid = targets[0];
            var cnt = counts.TryGetValue(uid, out var c) ? c : 0;
            total = cnt;
            points.Add(new StatisticDataPoint(periodLabel, cnt));
        }
        else
        {
            var emails = await LoadEmailsAsync(connection, targets, ct);
            foreach (var uid in targets)
            {
                var cnt = counts.TryGetValue(uid, out var c) ? c : 0;
                if (cnt == 0) continue;
                total += cnt;
                points.Add(new StatisticDataPoint(emails.GetValueOrDefault(uid) ?? "Technician", cnt));
            }
            points = points.OrderByDescending(p => p.Value).ToList();
        }

        return new StatisticTileData(
            TileId: tile.Id,
            MetricKey: tile.MetricKey,
            ChartType: tile.ChartType,
            Unit: "tickets",
            PeriodLabel: periodLabel,
            Total: total,
            Points: points,
            GeneratedUtc: DateTime.UtcNow);
    }

    /// Canonical lowercase uuid strings from a comma-separated status-id list
    /// (the Resolved/CWI tab settings), matching the form stored in
    /// ticket_events.metadata->>'to'.
    private static string[] ParseStatusIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var g)) set.Add(g.ToString());
        }
        return set.ToArray();
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
        var single = IsSingleScope(tile.Scope);

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
        else if (!IsSingleScope(tile.Scope))
        {
            // grouping = none + multi-technician scope (team / compare) → one
            // bar per technician so they can be compared, rather than a single
            // combined total.
            var perUser = (await connection.QueryAsync<(Guid UserId, long Minutes)>(new CommandDefinition(
                """
                SELECT e.user_id AS UserId, COALESCE(SUM(e.minutes), 0)::bigint AS Minutes
                FROM timesheet_entries e
                WHERE e.user_id = ANY(@ids) AND e.entry_date BETWEEN @from AND @to
                GROUP BY e.user_id
                """,
                args, cancellationToken: ct))).ToList();
            var emails = await LoadEmailsAsync(connection, targets, ct);
            points.AddRange(perUser
                .Where(r => r.Minutes > 0)
                .OrderByDescending(r => r.Minutes)
                .Select(r => new StatisticDataPoint(emails.GetValueOrDefault(r.UserId) ?? "Technician", ToHours(r.Minutes))));
        }
        else
        {
            // grouping = none, single scope → a single bucket carrying the period total.
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

            case StatisticScopes.Users:
                return ParseStatusGuids(tile.ScopeUserIds);

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
    /// the configured application timezone by the caller) and shifted by
    /// <paramref name="offset"/> whole period units (prev/next navigation).
    /// The label is the concrete period; offset 0 also carries a "this …" hint.
    private static (DateOnly From, DateOnly To, string Label) ResolvePeriod(string period, DateOnly today, int offset)
    {
        switch (period)
        {
            case StatisticPeriods.Day:
            {
                var d = today.AddDays(offset);
                var hint = offset == 0 ? "Today · " : offset == -1 ? "Yesterday · " : "";
                return (d, d, $"{hint}{d:ddd dd MMM yyyy}");
            }

            case StatisticPeriods.Week:
            {
                var refDay = today.AddDays(offset * 7);
                var monday = refDay.AddDays(-(((int)refDay.DayOfWeek + 6) % 7));
                var sunday = monday.AddDays(6);
                var hint = offset == 0 ? "This week · " : offset == -1 ? "Last week · " : "";
                return (monday, sunday, $"{hint}{monday:dd MMM}–{sunday:dd MMM yyyy}");
            }

            case StatisticPeriods.Year:
            {
                var year = today.Year + offset;
                var hint = offset == 0 ? "This year · " : "";
                return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), $"{hint}{year}");
            }

            case StatisticPeriods.Month:
            default:
            {
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(offset);
                var last = first.AddMonths(1).AddDays(-1);
                var hint = offset == 0 ? "This month · " : offset == -1 ? "Last month · " : "";
                return (first, last, $"{hint}{first:MMM yyyy}");
            }
        }
    }

    private static bool IsSingleScope(string scope) =>
        string.Equals(scope, StatisticScopes.ViewerSelf, StringComparison.Ordinal) ||
        string.Equals(scope, StatisticScopes.User, StringComparison.Ordinal);

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

    private static StatisticTileData Empty(StatisticTile tile, string periodLabel, string unit = "hours") =>
        new(tile.Id, tile.MetricKey, tile.ChartType, unit, periodLabel, 0,
            Array.Empty<StatisticDataPoint>(), DateTime.UtcNow);
}
