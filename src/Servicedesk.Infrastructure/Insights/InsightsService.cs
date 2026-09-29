using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Insights;

/// Which moment a ticket is counted on.
///   New    — created_utc.
///   Closed — the close moment <c>COALESCE(closed_utc, resolved_utc)</c> of a
///            ticket whose *current* state is Resolved or Closed (same rule
///            as the Reporting API: a reopened ticket is open, not closed;
///            tickets without a close stamp — some migrated ones — are not
///            countable and are left out).
public enum InsightsMetric { New, Closed }

/// Outcome filter for the Closed metric. Merged tickets sit in a Closed-
/// category status but are their own outcome (merged_into_ticket_id set).
[Flags]
public enum ClosedOutcomes
{
    None = 0,
    Resolved = 1,
    Closed = 2,
    Merged = 4,
    All = Resolved | Closed | Merged,
}

public sealed record InsightsTicketFilter(InsightsMetric Metric, ClosedOutcomes Outcomes)
{
    public static InsightsTicketFilter NewTickets { get; } = new(InsightsMetric.New, ClosedOutcomes.All);
}

/// <see cref="Slot"/> is the queue's fixed position in the caller's full
/// queue list; it picks the chart colour so a queue keeps its colour when
/// others are filtered out. Queue colours themselves are not used — installs
/// commonly leave every queue on the default colour, which would make a
/// stacked chart unreadable.
public sealed record InsightsQueue(Guid Id, string Name, bool IsActive, int Slot);

/// One bucket of a ticket-count report. <see cref="Counts"/> is aligned
/// index-for-index with <see cref="TicketCountReport.Queues"/>.
public sealed record TicketCountBucket(DateOnly Start, DateOnly From, DateOnly To, IReadOnlyList<int> Counts);

public sealed record TicketCountReport(
    InsightsTicketFilter Filter,
    InsightsRange Range,
    InsightsGranularity Granularity,
    string TimeZoneId,
    DateOnly Today,
    IReadOnlyList<InsightsQueue> Queues,
    IReadOnlyList<TicketCountBucket> Buckets);

/// One row of the ticket list under a report. <see cref="MomentUtc"/> is the
/// moment the ticket was counted on (created or closed).
public sealed record InsightsTicketItem(
    Guid Id,
    long Number,
    string Subject,
    string RequesterName,
    string? CompanyName,
    string StatusName,
    string StatusColor,
    string StatusCategory,
    DateTime MomentUtc);

public sealed record InsightsTicketPage(int Total, IReadOnlyList<InsightsTicketItem> Items);

/// The server's view of "now" for Insights: display zone + local date.
public sealed record InsightsClock(TimeZoneInfo TimeZone, DateOnly Today);

public interface IInsightsService
{
    Task<InsightsClock> GetClockAsync(CancellationToken ct = default);

    /// Tickets per bucket per queue for <paramref name="filter"/>. Queue
    /// access is applied in SQL via <paramref name="scope"/> — a non-admin
    /// never gets counts (or queue names) for queues outside their access
    /// list. Deleted tickets are excluded; merged tickets count.
    Task<TicketCountReport> GetTicketCountsAsync(
        InsightsTicketFilter filter, InsightsRange range, InsightsGranularity granularity,
        QueueAccessScope scope, CancellationToken ct = default);

    /// The tickets behind a report — same moment, window, exclusions and
    /// access scope — newest first. <paramref name="queueIds"/> narrows to
    /// the queues ticked on the page (null/empty = every visible queue); it
    /// can only ever narrow the access scope, never widen it.
    Task<InsightsTicketPage> ListTicketsAsync(
        InsightsTicketFilter filter, InsightsRange range, QueueAccessScope scope,
        IReadOnlyCollection<Guid>? queueIds, int offset, int limit, CancellationToken ct = default);
}

public sealed class InsightsService : IInsightsService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public InsightsService(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public async Task<InsightsClock> GetClockAsync(CancellationToken ct = default)
    {
        string? tzId = null;
        try { tzId = await _settings.GetAsync<string>(SettingKeys.App.TimeZone, ct); }
        catch { /* settings store hiccup — host zone below */ }
        var tz = InsightsCalendar.ResolveTimeZone(tzId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        return new InsightsClock(tz, today);
    }

    /// SQL pieces per filter. Every fragment is a compile-time constant
    /// chosen from the enum values — nothing caller-supplied is spliced in.
    /// The moment expressions match existing indexes: created_utc
    /// (ix_tickets_queue_created_live / ix_tickets_created_id) and
    /// COALESCE(closed_utc, resolved_utc) (ix_tickets_closed_moment).
    internal static (string Moment, string Filter) Fragments(InsightsTicketFilter filter)
    {
        if (filter.Metric == InsightsMetric.New)
            return ("t.created_utc", "");

        var moment = "COALESCE(t.closed_utc, t.resolved_utc)";
        var parts = new List<string>(3);
        if (filter.Outcomes.HasFlag(ClosedOutcomes.Resolved))
            parts.Add("(s.state_category = 'Resolved' AND t.merged_into_ticket_id IS NULL)");
        if (filter.Outcomes.HasFlag(ClosedOutcomes.Closed))
            parts.Add("(s.state_category = 'Closed' AND t.merged_into_ticket_id IS NULL)");
        if (filter.Outcomes.HasFlag(ClosedOutcomes.Merged))
            parts.Add("t.merged_into_ticket_id IS NOT NULL");
        if (parts.Count == 0) parts.Add("FALSE");

        return (moment,
            $" AND s.state_category IN ('Resolved', 'Closed') AND ({string.Join(" OR ", parts)})");
    }

    public async Task<TicketCountReport> GetTicketCountsAsync(
        InsightsTicketFilter filter, InsightsRange range, InsightsGranularity granularity,
        QueueAccessScope scope, CancellationToken ct = default)
    {
        var clock = await GetClockAsync(ct);
        var buckets = InsightsCalendar.BuildBuckets(range, granularity);
        var bounds = InsightsCalendar.BucketBoundsUtc(buckets, clock.TimeZone);
        var fromUtc = bounds[0];
        var toUtc = InsightsCalendar.LocalMidnightUtc(range.To.AddDays(1), clock.TimeZone);

        // Fixed fragments only — the access list is a bound parameter, never
        // string-built. Admins skip the filter entirely.
        var (moment, metricFilter) = Fragments(filter);
        var statusJoin = filter.Metric == InsightsMetric.New ? "" : " JOIN statuses s ON s.id = t.status_id";
        var ticketAccess = scope.IsAdmin ? "" : " AND t.queue_id = ANY(@Accessible)";
        var queueAccess = scope.IsAdmin
            ? "(q.is_active OR q.id = ANY(@WithTickets))"
            : "q.id = ANY(@Accessible)";

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // width_bucket returns the 1-based index i with bounds[i] <= x <
        // bounds[i+1]; the moment window guarantees every row lands in 1..n.
        var countSql = $"""
            SELECT t.queue_id AS QueueId,
                   width_bucket({moment}, @Bounds) AS Bucket,
                   COUNT(*)::int AS Count
              FROM tickets t{statusJoin}
             WHERE t.is_deleted = FALSE
               AND {moment} >= @FromUtc AND {moment} < @ToUtc{metricFilter}{ticketAccess}
             GROUP BY 1, 2
            """;
        var accessible = scope.ToArray();
        var counts = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            countSql,
            new { Bounds = bounds, FromUtc = fromUtc, ToUtc = toUtc, Accessible = accessible },
            cancellationToken: ct))).ToList();

        var withTickets = counts.Select(c => c.QueueId).Distinct().ToArray();
        var queueSql = $"""
            SELECT q.id AS Id, q.name::text AS Name, q.is_active AS IsActive
              FROM queues q
             WHERE {queueAccess}
             ORDER BY q.sort_order, q.name
            """;
        var queues = (await connection.QueryAsync<QueueRow>(new CommandDefinition(
            queueSql,
            new { Accessible = accessible, WithTickets = withTickets },
            cancellationToken: ct)))
            .Select((q, i) => new InsightsQueue(q.Id, q.Name, q.IsActive, i))
            .ToList();

        var queueIndex = queues.Select((q, i) => (q.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var grid = new int[buckets.Count][];
        for (var b = 0; b < buckets.Count; b++) grid[b] = new int[queues.Count];
        foreach (var row in counts)
        {
            if (!queueIndex.TryGetValue(row.QueueId, out var qi)) continue;
            var bi = Math.Clamp(row.Bucket - 1, 0, buckets.Count - 1);
            grid[bi][qi] += row.Count;
        }

        var result = buckets
            .Select((b, i) => new TicketCountBucket(b.Start, b.From, b.To, grid[i]))
            .ToList();
        return new TicketCountReport(filter, range, granularity, clock.TimeZone.Id, clock.Today, queues, result);
    }

    public async Task<InsightsTicketPage> ListTicketsAsync(
        InsightsTicketFilter filter, InsightsRange range, QueueAccessScope scope,
        IReadOnlyCollection<Guid>? queueIds, int offset, int limit, CancellationToken ct = default)
    {
        var clock = await GetClockAsync(ct);
        var fromUtc = InsightsCalendar.LocalMidnightUtc(range.From, clock.TimeZone);
        var toUtc = InsightsCalendar.LocalMidnightUtc(range.To.AddDays(1), clock.TimeZone);

        // Fixed fragments only; both id lists are bound parameters. The access
        // filter always applies to non-admins, the page selection on top.
        var (moment, metricFilter) = Fragments(filter);
        var access = scope.IsAdmin ? "" : " AND t.queue_id = ANY(@Accessible)";
        var selection = queueIds is { Count: > 0 } ? " AND t.queue_id = ANY(@Selected)" : "";
        var where = $"""

             WHERE t.is_deleted = FALSE
               AND {moment} >= @FromUtc AND {moment} < @ToUtc{metricFilter}{access}{selection}
            """;

        var sql = $"""
            SELECT COUNT(*)::int
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id{where};

            SELECT t.id AS Id,
                   t.number AS Number,
                   t.subject AS Subject,
                   COALESCE(NULLIF(TRIM(CONCAT_WS(' ', c.first_name, c.last_name)), ''), c.email::text, '') AS RequesterName,
                   co.name AS CompanyName,
                   s.name AS StatusName,
                   s.color AS StatusColor,
                   s.state_category AS StatusCategory,
                   {moment} AS MomentUtc
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
              LEFT JOIN contacts c ON c.id = t.requester_contact_id
              LEFT JOIN companies co ON co.id = t.company_id{where}
             ORDER BY {moment} DESC, t.number DESC
             LIMIT @Limit OFFSET @Offset;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
            sql,
            new
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Accessible = scope.ToArray(),
                Selected = queueIds?.ToArray() ?? Array.Empty<Guid>(),
                Limit = limit,
                Offset = offset,
            },
            cancellationToken: ct));
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<ListRow>())
            .Select(r => new InsightsTicketItem(
                r.Id, r.Number, r.Subject, r.RequesterName, r.CompanyName,
                r.StatusName, r.StatusColor, r.StatusCategory,
                DateTime.SpecifyKind(r.MomentUtc, DateTimeKind.Utc)))
            .ToList();
        return new InsightsTicketPage(total, items);
    }

    private sealed class ListRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public string Subject { get; set; } = "";
        public string RequesterName { get; set; } = "";
        public string? CompanyName { get; set; }
        public string StatusName { get; set; } = "";
        public string StatusColor { get; set; } = "";
        public string StatusCategory { get; set; } = "";
        public DateTime MomentUtc { get; set; }
    }

    private sealed class CountRow
    {
        public Guid QueueId { get; set; }
        public int Bucket { get; set; }
        public int Count { get; set; }
    }

    private sealed class QueueRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
    }
}
