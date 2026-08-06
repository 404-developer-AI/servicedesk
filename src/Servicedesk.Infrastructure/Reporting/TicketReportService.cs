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
        Guid? companyId,
        CancellationToken ct = default)
    {
        // One round trip for all six result sets. Counts are cast to int in
        // SQL (COUNT is bigint) so Dapper's scalar mapping stays exact.
        // Ordering is (timestamp, number) so offset-paging is stable while
        // new tickets keep arriving at the end. The company filter is a
        // fixed parameterized fragment appended to every WHERE — never
        // string-built from caller input.
        var companyFilter = companyId is null ? "" : " AND t.company_id = @CompanyId";

        var sql = $"""
            SELECT COUNT(*)::int
              FROM tickets t
             WHERE t.is_deleted = FALSE
               AND t.created_utc >= @From AND t.created_utc < @To{companyFilter};

            SELECT t.number AS Number, t.subject AS Subject
              FROM tickets t
             WHERE t.is_deleted = FALSE
               AND t.created_utc >= @From AND t.created_utc < @To{companyFilter}
             ORDER BY t.created_utc, t.number
             LIMIT @Limit OFFSET @OpenedOffset;

            SELECT COUNT(*)::int
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category IN ('Resolved', 'Closed')
               AND COALESCE(t.closed_utc, t.resolved_utc) >= @From
               AND COALESCE(t.closed_utc, t.resolved_utc) < @To{companyFilter};

            SELECT t.number AS Number, t.subject AS Subject
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category IN ('Resolved', 'Closed')
               AND COALESCE(t.closed_utc, t.resolved_utc) >= @From
               AND COALESCE(t.closed_utc, t.resolved_utc) < @To{companyFilter}
             ORDER BY COALESCE(t.closed_utc, t.resolved_utc), t.number
             LIMIT @Limit OFFSET @ClosedOffset;

            SELECT COUNT(*)::int
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category NOT IN ('Resolved', 'Closed'){companyFilter};

            SELECT t.number AS Number, t.subject AS Subject
              FROM tickets t
              JOIN statuses s ON s.id = t.status_id
             WHERE t.is_deleted = FALSE
               AND s.state_category NOT IN ('Resolved', 'Closed'){companyFilter}
             ORDER BY t.created_utc, t.number
             LIMIT @Limit OFFSET @OpenOffset;
            """;

        var args = new
        {
            From = fromUtc,
            To = toUtc,
            CompanyId = companyId,
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

    public async Task<CompanyReportList> ListCompaniesAsync(
        int maxItems,
        int offset,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*)::int FROM companies;

            SELECT id        AS Id,
                   name      AS Name,
                   code      AS Code,
                   is_active AS IsActive
              FROM companies
             ORDER BY name, id
             LIMIT @Limit OFFSET @Offset;
            """;

        var args = new
        {
            Limit = Math.Max(0, maxItems),
            Offset = Math.Max(0, offset),
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var multi = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, args, cancellationToken: ct));

        var count = await multi.ReadFirstAsync<int>();
        var rows = (await multi.ReadAsync<CompanyRow>()).ToList();
        var items = rows
            .Select(r => new CompanyReportItem(r.Id, r.Name, r.Code, r.IsActive))
            .ToList();

        return new CompanyReportList(
            Count: count,
            Items: items,
            Offset: args.Offset,
            Truncated: args.Offset + items.Count < count);
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

    private sealed class CompanyRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Code { get; init; }
        public bool IsActive { get; init; }
    }
}
