using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Insights;

/// An agent picked for the Agents overview. <see cref="Slot"/> is the
/// position in the caller's selection and picks the chart colour.
public sealed record InsightsAgent(Guid Id, string Name, string Email, string Role, int Slot);

/// Headline numbers for one agent over the whole period. Tickets is the
/// distinct count over the period (not the sum of the buckets — a ticket
/// worked on Monday and Tuesday counts once).
public sealed record AgentActivityTotals(
    int Tickets,
    int TicketMinutes,
    int Calls,
    int CallsIn,
    int CallsOut,
    long CallSeconds,
    int OpenedNoAction = 0)
{
    public static AgentActivityTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// One bucket of the Agents overview. <see cref="Values"/> is aligned
/// index-for-index with <see cref="AgentActivityReport.Agents"/>.
public sealed record AgentActivityBucket(
    DateOnly Start, DateOnly From, DateOnly To, IReadOnlyList<AgentActivityTotals> Values);

public sealed record AgentActivityReport(
    InsightsRange Range,
    InsightsGranularity Granularity,
    string TimeZoneId,
    DateOnly Today,
    IReadOnlyList<InsightsAgent> Agents,
    IReadOnlyList<AgentActivityTotals> Totals,
    IReadOnlyList<AgentActivityBucket> Buckets);

/// Sort order of an agent's ticket list. Fixed server-side choices only.
public enum AgentTicketSort { Recent, PeriodTime, AgentTime, TicketTime }

/// One ticket an agent worked on in the period. Minutes: the agent's
/// time in the period, the agent's time on the ticket ever, and the
/// ticket's total time across every agent. <see cref="LastActionUtc"/> is
/// the agent's last ticket action in the period (null when the agent only
/// logged time); <see cref="LastEntryDate"/> the last day they logged time.
public sealed record AgentTicketItem(
    Guid Id,
    long Number,
    string Subject,
    string RequesterName,
    string? CompanyName,
    string StatusName,
    string StatusColor,
    string StatusCategory,
    DateTime? LastActionUtc,
    DateOnly? LastEntryDate,
    int PeriodMinutes,
    int AgentMinutes,
    int TicketMinutes);

public sealed record AgentTicketPage(int Total, IReadOnlyList<AgentTicketItem> Items);

/// One time an agent opened a ticket and closed it again (it left their
/// recent-tickets list) without acting on it in between.
/// <see cref="CloseReason"/> is removed / cleared / evicted.
public sealed record OpenedNoActionItem(
    long SessionId,
    Guid TicketId,
    long Number,
    string Subject,
    string? CompanyName,
    string StatusName,
    string StatusColor,
    string StatusCategory,
    DateTime OpenedUtc,
    DateTime ClosedUtc,
    string CloseReason,
    int DurationSeconds);

public sealed record OpenedNoActionPage(int Total, IReadOnlyList<OpenedNoActionItem> Items);

/// v0.1.14 — the Agents overview of Insights: what one to a few agents did
/// in a period, side by side.
///
/// "Worked on a ticket" = the agent authored a ticket event in the window
/// (status / queue / assignment / priority / note / reply / mail / …; the
/// inbound <c>MailReceived</c> kind is excluded) or logged time on it that
/// day. Read from <c>ticket_events</c>, not the activity feed, so ticket
/// history is not cut off by the feed's retention. Calls come from the
/// <c>telavox_call_completed</c> feed rows — the only lasting call record
/// (answered calls the poller saw; feed retention applies).
///
/// Ticket figures are scoped to the caller's queue access on the ticket's
/// current queue; call figures are not ticket-bound.
///
/// "Opened without action" reads <c>ticket_open_sessions</c> (the recent-
/// tickets sidebar list: in = opened, out = closed). It counts sessions
/// closed in the window, at least <c>minSeconds</c> long, with no ticket
/// event and no timesheet entry created by that agent on that ticket
/// between open and close — the same notion of "action" as "worked on".
/// Later work on the ticket never removes the row: only the session's own
/// window counts.
public interface IAgentInsightsService
{
    /// Resolves the requested ids to Agent/Admin users, in request order.
    /// Unknown ids and non-staff users are dropped — the caller compares
    /// counts to reject the request.
    Task<IReadOnlyList<InsightsAgent>> ResolveAgentsAsync(
        IReadOnlyList<Guid> ids, CancellationToken ct = default);

    Task<AgentActivityReport> GetActivityAsync(
        IReadOnlyList<InsightsAgent> agents, InsightsRange range, InsightsGranularity granularity,
        InsightsClock clock, QueueAccessScope scope, int minSeconds, CancellationToken ct = default);

    Task<OpenedNoActionPage> ListOpenedWithoutActionAsync(
        Guid agentId, InsightsRange range, InsightsClock clock, QueueAccessScope scope,
        int minSeconds, int offset, int limit, CancellationToken ct = default);

    Task<AgentTicketPage> ListTicketsAsync(
        Guid agentId, InsightsRange range, InsightsClock clock, QueueAccessScope scope,
        AgentTicketSort sort, int offset, int limit, CancellationToken ct = default);
}

public sealed class AgentInsightsService : IAgentInsightsService
{
    /// Inbound mail is not something the agent did.
    private const string ExcludedEventTypes = "'MailReceived'";
    private const string CallEventType = "telavox_call_completed";

    /// Sessions of <c>o.user_id</c> long enough to count, with no action of
    /// that agent on that ticket inside the session window. Shared by the
    /// count and the list.
    private const string NoActionPredicate = $"""

               AND EXTRACT(EPOCH FROM (o.closed_utc - o.opened_utc)) >= @MinSeconds
               AND NOT EXISTS (
                   SELECT 1 FROM ticket_events e
                    WHERE e.ticket_id = o.ticket_id
                      AND e.author_user_id = o.user_id
                      AND e.created_utc >= o.opened_utc AND e.created_utc <= o.closed_utc
                      AND e.event_type NOT IN ({ExcludedEventTypes}))
               AND NOT EXISTS (
                   SELECT 1 FROM timesheet_entries te
                    WHERE te.ticket_id = o.ticket_id
                      AND te.user_id = o.user_id
                      AND te.created_utc >= o.opened_utc AND te.created_utc <= o.closed_utc)
        """;

    private readonly NpgsqlDataSource _dataSource;

    public AgentInsightsService(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<InsightsAgent>> ResolveAgentsAsync(
        IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<InsightsAgent>();

        const string sql = """
            SELECT u.id AS Id,
                   COALESCE(NULLIF(TRIM(u.display_name), ''), u.email::text) AS Name,
                   u.email::text AS Email,
                   u.role_name AS Role
              FROM users u
             WHERE u.id = ANY(@Ids)
               AND u.role_name IN ('Agent', 'Admin')
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<AgentRow>(new CommandDefinition(
            sql, new { Ids = ids.Distinct().ToArray() }, cancellationToken: ct)))
            .ToDictionary(r => r.Id);

        var result = new List<InsightsAgent>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            if (rows.TryGetValue(id, out var r))
                result.Add(new InsightsAgent(r.Id, r.Name, r.Email, r.Role, result.Count));
        }
        return result;
    }

    public async Task<AgentActivityReport> GetActivityAsync(
        IReadOnlyList<InsightsAgent> agents, InsightsRange range, InsightsGranularity granularity,
        InsightsClock clock, QueueAccessScope scope, int minSeconds, CancellationToken ct = default)
    {
        var buckets = InsightsCalendar.BuildBuckets(range, granularity);
        var bounds = InsightsCalendar.BucketBoundsUtc(buckets, clock.TimeZone);
        // Timesheet rows carry a local calendar date, so they bucket on the
        // bucket's first day (timestamp without zone), not a UTC instant.
        var dateBounds = buckets
            .Select(b => b.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified))
            .ToArray();
        var fromUtc = bounds[0];
        var toUtc = InsightsCalendar.LocalMidnightUtc(range.To.AddDays(1), clock.TimeZone);

        // Fixed fragments only — the access list is a bound parameter.
        var access = scope.IsAdmin ? "" : " AND t.queue_id = ANY(@Accessible)";

        // GROUPING SETS: one row per (agent, bucket) plus one period total per
        // agent (Bucket NULL) — the distinct ticket count over the period is
        // not the sum of the buckets.
        var sql = $"""
            WITH touched AS (
                SELECT e.author_user_id AS user_id, e.ticket_id,
                       width_bucket(e.created_utc, @Bounds) AS b, 0 AS minutes
                  FROM ticket_events e
                 WHERE e.author_user_id = ANY(@Agents)
                   AND e.created_utc >= @FromUtc AND e.created_utc < @ToUtc
                   AND e.event_type NOT IN ({ExcludedEventTypes})
                UNION ALL
                SELECT te.user_id, te.ticket_id,
                       width_bucket(te.entry_date::timestamp, @DateBounds) AS b, te.minutes
                  FROM timesheet_entries te
                 WHERE te.user_id = ANY(@Agents)
                   AND te.ticket_id IS NOT NULL
                   AND te.entry_date >= @FromDate AND te.entry_date <= @ToDate
            )
            SELECT x.user_id AS UserId,
                   x.b AS Bucket,
                   COUNT(DISTINCT x.ticket_id)::int AS Tickets,
                   COALESCE(SUM(x.minutes), 0)::int AS Minutes
              FROM touched x
              JOIN tickets t ON t.id = x.ticket_id
             WHERE t.is_deleted = FALSE{access}
             GROUP BY GROUPING SETS ((x.user_id, x.b), (x.user_id));

            SELECT c.agent_id AS UserId,
                   c.b AS Bucket,
                   COUNT(*)::int AS Calls,
                   COUNT(*) FILTER (WHERE c.direction = 'incoming')::int AS CallsIn,
                   COUNT(*) FILTER (WHERE c.direction = 'outgoing')::int AS CallsOut,
                   COALESCE(SUM(c.seconds), 0)::bigint AS CallSeconds
              FROM (
                SELECT a.agent_id,
                       width_bucket(a.occurred_utc, @Bounds) AS b,
                       a.metadata->>'direction' AS direction,
                       CASE WHEN jsonb_typeof(a.metadata->'durationSeconds') = 'number'
                            THEN (a.metadata->>'durationSeconds')::bigint END AS seconds
                  FROM agent_activity_events a
                 WHERE a.agent_id = ANY(@Agents)
                   AND a.event_type = @CallEventType
                   AND a.occurred_utc >= @FromUtc AND a.occurred_utc < @ToUtc
              ) c
             GROUP BY GROUPING SETS ((c.agent_id, c.b), (c.agent_id));

            SELECT n.user_id AS UserId, n.b AS Bucket, COUNT(*)::int AS Count
              FROM (
                SELECT o.user_id, width_bucket(o.closed_utc, @Bounds) AS b
                  FROM ticket_open_sessions o
                  JOIN tickets t ON t.id = o.ticket_id
                 WHERE o.user_id = ANY(@Agents)
                   AND o.closed_utc >= @FromUtc AND o.closed_utc < @ToUtc
                   AND t.is_deleted = FALSE{access}{NoActionPredicate}
              ) n
             GROUP BY GROUPING SETS ((n.user_id, n.b), (n.user_id));
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            sql,
            new
            {
                Agents = agents.Select(a => a.Id).ToArray(),
                Bounds = bounds,
                DateBounds = dateBounds,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                FromDate = range.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                ToDate = range.To.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                Accessible = scope.ToArray(),
                CallEventType,
                MinSeconds = (double)Math.Max(0, minSeconds),
            },
            cancellationToken: ct));
        var ticketRows = (await multi.ReadAsync<TicketActivityRow>()).ToList();
        var callRows = (await multi.ReadAsync<CallRow>()).ToList();
        var openedRows = (await multi.ReadAsync<OpenedRow>()).ToList();

        return Assemble(agents, range, granularity, clock, buckets, ticketRows, callRows, openedRows);
    }

    /// Folds the grouped SQL rows into the per-agent grid. Pure — unit-tested.
    internal static AgentActivityReport Assemble(
        IReadOnlyList<InsightsAgent> agents, InsightsRange range, InsightsGranularity granularity,
        InsightsClock clock, IReadOnlyList<InsightsBucket> buckets,
        IEnumerable<TicketActivityRow> ticketRows, IEnumerable<CallRow> callRows,
        IEnumerable<OpenedRow>? openedRows = null)
    {
        var index = agents.Select((a, i) => (a.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var totals = agents.Select(_ => new Acc()).ToArray();
        var grid = new Acc[buckets.Count][];
        for (var b = 0; b < buckets.Count; b++)
            grid[b] = agents.Select(_ => new Acc()).ToArray();

        Acc? Cell(Guid userId, int? bucket)
        {
            if (!index.TryGetValue(userId, out var ai)) return null;
            if (bucket is null) return totals[ai];
            // Rows outside 1..n cannot occur inside the window; clamp anyway.
            return grid[Math.Clamp(bucket.Value - 1, 0, buckets.Count - 1)][ai];
        }

        foreach (var r in ticketRows)
        {
            if (Cell(r.UserId, r.Bucket) is not { } acc) continue;
            acc.Tickets += r.Tickets;
            acc.Minutes += r.Minutes;
        }
        foreach (var r in callRows)
        {
            if (Cell(r.UserId, r.Bucket) is not { } acc) continue;
            acc.Calls += r.Calls;
            acc.CallsIn += r.CallsIn;
            acc.CallsOut += r.CallsOut;
            acc.CallSeconds += r.CallSeconds;
        }
        foreach (var r in openedRows ?? Enumerable.Empty<OpenedRow>())
        {
            if (Cell(r.UserId, r.Bucket) is not { } acc) continue;
            acc.OpenedNoAction += r.Count;
        }

        return new AgentActivityReport(
            range, granularity, clock.TimeZone.Id, clock.Today, agents,
            totals.Select(a => a.ToTotals()).ToList(),
            buckets.Select((b, i) => new AgentActivityBucket(
                b.Start, b.From, b.To, grid[i].Select(a => a.ToTotals()).ToList())).ToList());
    }

    public async Task<AgentTicketPage> ListTicketsAsync(
        Guid agentId, InsightsRange range, InsightsClock clock, QueueAccessScope scope,
        AgentTicketSort sort, int offset, int limit, CancellationToken ct = default)
    {
        var fromUtc = InsightsCalendar.LocalMidnightUtc(range.From, clock.TimeZone);
        var toUtc = InsightsCalendar.LocalMidnightUtc(range.To.AddDays(1), clock.TimeZone);
        var access = scope.IsAdmin ? "" : " AND t.queue_id = ANY(@Accessible)";
        var orderBy = OrderBy(sort);

        // The same CTE heads both statements (count + page); only fixed
        // fragments are spliced in, every value is a bound parameter.
        var cte = $"""
            WITH touched AS (
                SELECT e.ticket_id, MAX(e.created_utc) AS last_utc, NULL::date AS last_date
                  FROM ticket_events e
                 WHERE e.author_user_id = @Agent
                   AND e.created_utc >= @FromUtc AND e.created_utc < @ToUtc
                   AND e.event_type NOT IN ({ExcludedEventTypes})
                 GROUP BY e.ticket_id
                UNION ALL
                SELECT te.ticket_id, NULL::timestamptz, MAX(te.entry_date)
                  FROM timesheet_entries te
                 WHERE te.user_id = @Agent
                   AND te.ticket_id IS NOT NULL
                   AND te.entry_date >= @FromDate AND te.entry_date <= @ToDate
                 GROUP BY te.ticket_id
            ),
            per_ticket AS (
                SELECT x.ticket_id, MAX(x.last_utc) AS last_utc, MAX(x.last_date) AS last_date
                  FROM touched x
                  JOIN tickets t ON t.id = x.ticket_id
                 WHERE t.is_deleted = FALSE{access}
                 GROUP BY x.ticket_id
            )
            """;

        var sql = $"""
            {cte}
            SELECT COUNT(*)::int FROM per_ticket;

            {cte},
            mins AS (
                SELECT te.ticket_id,
                       COALESCE(SUM(te.minutes) FILTER (
                           WHERE te.user_id = @Agent
                             AND te.entry_date >= @FromDate AND te.entry_date <= @ToDate), 0)::int AS period_m,
                       COALESCE(SUM(te.minutes) FILTER (WHERE te.user_id = @Agent), 0)::int AS agent_m,
                       COALESCE(SUM(te.minutes), 0)::int AS ticket_m
                  FROM timesheet_entries te
                 WHERE te.ticket_id IN (SELECT ticket_id FROM per_ticket)
                 GROUP BY te.ticket_id
            )
            SELECT t.id AS Id,
                   t.number AS Number,
                   t.subject AS Subject,
                   COALESCE(NULLIF(TRIM(CONCAT_WS(' ', c.first_name, c.last_name)), ''), c.email::text, '') AS RequesterName,
                   co.name AS CompanyName,
                   s.name AS StatusName,
                   s.color AS StatusColor,
                   s.state_category AS StatusCategory,
                   p.last_utc AS LastActionUtc,
                   p.last_date::timestamp AS LastEntryDate,
                   COALESCE(m.period_m, 0) AS PeriodMinutes,
                   COALESCE(m.agent_m, 0) AS AgentMinutes,
                   COALESCE(m.ticket_m, 0) AS TicketMinutes
              FROM per_ticket p
              JOIN tickets t ON t.id = p.ticket_id
              JOIN statuses s ON s.id = t.status_id
              LEFT JOIN contacts c ON c.id = t.requester_contact_id
              LEFT JOIN companies co ON co.id = t.company_id
              LEFT JOIN mins m ON m.ticket_id = p.ticket_id
             ORDER BY {orderBy}, t.number DESC
             LIMIT @Limit OFFSET @Offset;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            sql,
            new
            {
                Agent = agentId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                FromDate = range.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                ToDate = range.To.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                Accessible = scope.ToArray(),
                Limit = limit,
                Offset = offset,
            },
            cancellationToken: ct));
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<TicketListRow>())
            .Select(r => new AgentTicketItem(
                r.Id, r.Number, r.Subject, r.RequesterName, r.CompanyName,
                r.StatusName, r.StatusColor, r.StatusCategory,
                r.LastActionUtc is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null,
                r.LastEntryDate is { } d ? DateOnly.FromDateTime(d) : null,
                r.PeriodMinutes, r.AgentMinutes, r.TicketMinutes))
            .ToList();
        return new AgentTicketPage(total, items);
    }

    public async Task<OpenedNoActionPage> ListOpenedWithoutActionAsync(
        Guid agentId, InsightsRange range, InsightsClock clock, QueueAccessScope scope,
        int minSeconds, int offset, int limit, CancellationToken ct = default)
    {
        var fromUtc = InsightsCalendar.LocalMidnightUtc(range.From, clock.TimeZone);
        var toUtc = InsightsCalendar.LocalMidnightUtc(range.To.AddDays(1), clock.TimeZone);
        var access = scope.IsAdmin ? "" : " AND t.queue_id = ANY(@Accessible)";

        // Fixed fragments only; every value is a bound parameter.
        var where = $"""

             WHERE o.user_id = @Agent
               AND o.closed_utc >= @FromUtc AND o.closed_utc < @ToUtc
               AND t.is_deleted = FALSE{access}{NoActionPredicate}
            """;

        var sql = $"""
            SELECT COUNT(*)::int
              FROM ticket_open_sessions o
              JOIN tickets t ON t.id = o.ticket_id{where};

            SELECT o.id AS SessionId,
                   t.id AS TicketId,
                   t.number AS Number,
                   t.subject AS Subject,
                   co.name AS CompanyName,
                   s.name AS StatusName,
                   s.color AS StatusColor,
                   s.state_category AS StatusCategory,
                   o.opened_utc AS OpenedUtc,
                   o.closed_utc AS ClosedUtc,
                   o.close_reason AS CloseReason,
                   LEAST(EXTRACT(EPOCH FROM (o.closed_utc - o.opened_utc)), 2147483647)::int AS DurationSeconds
              FROM ticket_open_sessions o
              JOIN tickets t ON t.id = o.ticket_id
              JOIN statuses s ON s.id = t.status_id
              LEFT JOIN companies co ON co.id = t.company_id{where}
             ORDER BY o.closed_utc DESC, o.id DESC
             LIMIT @Limit OFFSET @Offset;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            sql,
            new
            {
                Agent = agentId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Accessible = scope.ToArray(),
                MinSeconds = (double)Math.Max(0, minSeconds),
                Limit = limit,
                Offset = offset,
            },
            cancellationToken: ct));
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<OpenedListRow>())
            .Select(r => new OpenedNoActionItem(
                r.SessionId, r.TicketId, r.Number, r.Subject, r.CompanyName,
                r.StatusName, r.StatusColor, r.StatusCategory,
                DateTime.SpecifyKind(r.OpenedUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(r.ClosedUtc, DateTimeKind.Utc),
                r.CloseReason, r.DurationSeconds))
            .ToList();
        return new OpenedNoActionPage(total, items);
    }

    /// Fixed ORDER BY per sort. "Recent" puts the latest of the last action
    /// and the last time-entry day first; a date compares as its midnight.
    internal static string OrderBy(AgentTicketSort sort) => sort switch
    {
        AgentTicketSort.PeriodTime => "COALESCE(m.period_m, 0) DESC",
        AgentTicketSort.AgentTime => "COALESCE(m.agent_m, 0) DESC",
        AgentTicketSort.TicketTime => "COALESCE(m.ticket_m, 0) DESC",
        _ => "GREATEST(COALESCE(p.last_utc, '-infinity'::timestamptz), COALESCE(p.last_date::timestamptz, '-infinity'::timestamptz)) DESC",
    };

    private sealed class Acc
    {
        public int Tickets;
        public int Minutes;
        public int Calls;
        public int CallsIn;
        public int CallsOut;
        public long CallSeconds;
        public int OpenedNoAction;

        public AgentActivityTotals ToTotals() =>
            new(Tickets, Minutes, Calls, CallsIn, CallsOut, CallSeconds, OpenedNoAction);
    }

    private sealed class AgentRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
    }

    internal sealed class TicketActivityRow
    {
        public Guid UserId { get; set; }
        public int? Bucket { get; set; }
        public int Tickets { get; set; }
        public int Minutes { get; set; }
    }

    internal sealed class OpenedRow
    {
        public Guid UserId { get; set; }
        public int? Bucket { get; set; }
        public int Count { get; set; }
    }

    internal sealed class CallRow
    {
        public Guid UserId { get; set; }
        public int? Bucket { get; set; }
        public int Calls { get; set; }
        public int CallsIn { get; set; }
        public int CallsOut { get; set; }
        public long CallSeconds { get; set; }
    }

    private sealed class OpenedListRow
    {
        public long SessionId { get; set; }
        public Guid TicketId { get; set; }
        public long Number { get; set; }
        public string Subject { get; set; } = "";
        public string? CompanyName { get; set; }
        public string StatusName { get; set; } = "";
        public string StatusColor { get; set; } = "";
        public string StatusCategory { get; set; } = "";
        public DateTime OpenedUtc { get; set; }
        public DateTime ClosedUtc { get; set; }
        public string CloseReason { get; set; } = "";
        public int DurationSeconds { get; set; }
    }

    private sealed class TicketListRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public string Subject { get; set; } = "";
        public string RequesterName { get; set; } = "";
        public string? CompanyName { get; set; }
        public string StatusName { get; set; } = "";
        public string StatusColor { get; set; } = "";
        public string StatusCategory { get; set; } = "";
        public DateTime? LastActionUtc { get; set; }
        public DateTime? LastEntryDate { get; set; }
        public int PeriodMinutes { get; set; }
        public int AgentMinutes { get; set; }
        public int TicketMinutes { get; set; }
    }
}
