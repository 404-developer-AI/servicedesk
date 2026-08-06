using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Reporting;

public sealed class TicketReportService : ITicketReportService
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketReportService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<TicketPeriodReport> GetPeriodReportAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxItems,
        int openedOffset,
        int closedOffset,
        int openOffset,
        CancellationToken ct = default)
    {
        // One round trip for all six result sets. Counts are cast to int in
        // SQL (COUNT is bigint) so Dapper's scalar mapping stays exact.
        // Ordering is (timestamp, number) so offset-paging is stable while
        // new tickets keep arriving at the end.
        const string sql = """
            SELECT COUNT(*)::int
              FROM tickets t
             WHERE t.is_deleted = FALSE
               AND t.created_utc >= @From AND t.created_utc < @To;

            SELECT t.number AS Number, t.subject AS Subject
              FROM tickets t
             WHERE t.is_deleted = FALSE
               AND t.created_utc >= @From AND t.created_utc < @To
             ORDER BY t.created_utc, t.number
             LIMIT @Limit OFFSET @OpenedOffset;

            SELECT COUNT(*)::int
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category IN ('Resolved', 'Closed')
               AND COALESCE(t.closed_utc, t.resolved_utc) >= @From
               AND COALESCE(t.closed_utc, t.resolved_utc) < @To;

            SELECT t.number AS Number, t.subject AS Subject
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category IN ('Resolved', 'Closed')
               AND COALESCE(t.closed_utc, t.resolved_utc) >= @From
               AND COALESCE(t.closed_utc, t.resolved_utc) < @To
             ORDER BY COALESCE(t.closed_utc, t.resolved_utc), t.number
             LIMIT @Limit OFFSET @ClosedOffset;

            SELECT COUNT(*)::int
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category NOT IN ('Resolved', 'Closed');

            SELECT t.number AS Number, t.subject AS Subject
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category NOT IN ('Resolved', 'Closed')
             ORDER BY t.created_utc, t.number
             LIMIT @Limit OFFSET @OpenOffset;
            """;

        var args = new
        {
            From = fromUtc,
            To = toUtc,
            Limit = Math.Max(0, maxItems),
            OpenedOffset = Math.Max(0, openedOffset),
            ClosedOffset = Math.Max(0, closedOffset),
            OpenOffset = Math.Max(0, openOffset),
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var multi = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, args, cancellationToken: ct));

        var opened = await ReadSectionAsync(multi, args.OpenedOffset);
        var closed = await ReadSectionAsync(multi, args.ClosedOffset);
        var openNow = await ReadSectionAsync(multi, args.OpenOffset);

        return new TicketPeriodReport(opened, closed, openNow);
    }

    private static async Task<TicketReportSection> ReadSectionAsync(
        SqlMapper.GridReader multi, int offset)
    {
        var count = await multi.ReadFirstAsync<int>();
        var rows = (await multi.ReadAsync<Row>()).ToList();
        var items = rows
            .Select(r => new TicketReportItem(r.Number, r.Subject))
            .ToList();
        return new TicketReportSection(
            Count: count,
            Items: items,
            Offset: offset,
            Truncated: offset + items.Count < count);
    }

    private sealed class Row
    {
        public long Number { get; init; }
        public string Subject { get; init; } = string.Empty;
    }
}
