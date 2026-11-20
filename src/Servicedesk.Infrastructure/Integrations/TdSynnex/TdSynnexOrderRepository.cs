using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Dashboard bucket filter. Fixed set — mapped onto constant SQL, never
/// interpolated user input.
public static class TdSynnexOrderBucket
{
    public const string Active = "active";        // OPEN / INVESTIGATION / SHIPPING
    public const string Late = "late";            // active + an open ship date before today
    public const string Due = "due";              // active + an open ship date in the next 7 days
    public const string Open = "open";
    public const string Investigation = "investigation";
    public const string Shipping = "shipping";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string All = "all";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Active, Late, Due, Open, Investigation, Shipping, Completed, Rejected, All,
    };
}

public class TdSynnexOrderRow
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string? CustomerOrderNumber { get; set; }
    public string? EndUserOrderNumber { get; set; }
    public string? OrderType { get; set; }
    public string? Status { get; set; }
    public string? StatusDetail { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? OrderCreatedUtc { get; set; }
    public DateTime? OrderModifiedUtc { get; set; }
    public string? ShipToName { get; set; }
    public string? ShipToCity { get; set; }
    public string? ShipToCountry { get; set; }
    public decimal? TotalNet { get; set; }
    public string? Currency { get; set; }
    public int LineCount { get; set; }
    public decimal QuantityTotal { get; set; }
    /// DATE columns, read as midnight DateTime (Dapper has no DateOnly map).
    public DateTime? FirstShipDate { get; set; }
    public DateTime? EtaDate { get; set; }
    public DateTime LastSyncedUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }
    public int LinkedTicketCount { get; set; }
}

public sealed record TdSynnexOrderDetail(
    TdSynnexOrderRow Header,
    IReadOnlyList<TdSynnexOrderLine> Lines,
    TdSynnexShipTo? ShipTo,
    TdSynnexPayment? Payment,
    IReadOnlyList<TdSynnexOrderChangeRow> Changes);

public sealed class TdSynnexOrderChangeRow
{
    public long Id { get; set; }
    public DateTime DetectedUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = "[]";
}

/// What the sync needs to diff an incoming order against the mirror.
public sealed record TdSynnexStoredOrder(Guid Id, TdSynnexOrder Order, byte[]? SnapshotHash);

public sealed record TdSynnexOrderListResult(IReadOnlyList<TdSynnexOrderRow> Items, int Total, int Page, int PageSize);

public sealed record TdSynnexOrderStats(int Open, int Investigation, int Shipping, int Late, int DueThisWeek, int CompletedLast30Days, decimal OpenValue, string? Currency);

public sealed class TdSynnexLinkedTicketRow
{
    public Guid TicketId { get; set; }
    public long Number { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string StatusName { get; set; } = string.Empty;
    public string StatusColor { get; set; } = string.Empty;
}

public sealed record TdSynnexLinkedTickets(IReadOnlyList<TdSynnexLinkedTicketRow> Visible, int HiddenCount);

public sealed class TdSynnexSyncStateRow
{
    public DateTime? DeltaCursorUtc { get; set; }
    public DateTime? LastActiveRefreshUtc { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public int LastSeen { get; set; }
    public int LastUpserted { get; set; }
    public int LastChanged { get; set; }
    public int DurationMs { get; set; }
    public int OrderCount { get; set; }
}

public interface ITdSynnexOrderRepository
{
    Task<IReadOnlyDictionary<string, TdSynnexStoredOrder>> GetStoredAsync(IReadOnlyCollection<string> orderNumbers, CancellationToken ct);
    Task<Guid> UpsertAsync(TdSynnexOrder order, TdSynnexOrderDerived derived, bool changed, DateTime nowUtc, CancellationToken ct);
    Task AddChangeAsync(Guid orderId, DateTime nowUtc, string summary, IReadOnlyList<TdSynnexOrderChange> changes, CancellationToken ct);
    Task<int> PurgeChangesAsync(DateTime cutoffUtc, CancellationToken ct);

    Task<TdSynnexSyncStateRow?> GetSyncStateAsync(CancellationToken ct);
    Task SaveSyncStateAsync(TdSynnexSyncStateRow state, CancellationToken ct);

    Task<TdSynnexOrderListResult> ListAsync(string? search, string bucket, DateOnly today, string? sort, string? dir, int page, int pageSize, CancellationToken ct);
    Task<TdSynnexOrderStats> GetStatsAsync(DateOnly today, CancellationToken ct);
    Task<IReadOnlyList<TdSynnexOrderRow>> PickAsync(string? query, int limit, CancellationToken ct);
    Task<TdSynnexOrderDetail?> GetDetailAsync(Guid id, int changeLimit, CancellationToken ct);

    Task<IReadOnlyList<TdSynnexOrderDetail>> ListForTicketAsync(Guid ticketId, int changeLimit, CancellationToken ct);
    Task<bool> LinkAsync(Guid ticketId, Guid orderId, Guid? userId, CancellationToken ct);
    Task<bool> UnlinkAsync(Guid ticketId, Guid orderId, CancellationToken ct);
    Task<int> CountLinksForTicketAsync(Guid ticketId, CancellationToken ct);
    Task<TdSynnexLinkedTickets> GetLinkedTicketsAsync(Guid orderId, QueueAccessScope scope, CancellationToken ct);

    /// Every non-deleted ticket linked to the order — system path only (the
    /// trigger pass), never for a user-facing read.
    Task<IReadOnlyList<Guid>> GetLinkedTicketIdsAsync(Guid orderId, CancellationToken ct);
}

public sealed class TdSynnexOrderRepository : ITdSynnexOrderRepository
{
    /// Lines / ship-to / payment are persisted with these options and read
    /// back with them — one shape for the mirror, the diff and the API.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _ds;

    public TdSynnexOrderRepository(NpgsqlDataSource ds) => _ds = ds;

    private const string HeaderColumns = """
        o.id                    AS Id,
        o.order_number          AS OrderNumber,
        o.customer_order_number AS CustomerOrderNumber,
        o.end_user_order_number AS EndUserOrderNumber,
        o.order_type            AS OrderType,
        o.status                AS Status,
        o.status_detail         AS StatusDetail,
        o.created_by            AS CreatedBy,
        o.order_created_utc     AS OrderCreatedUtc,
        o.order_modified_utc    AS OrderModifiedUtc,
        o.ship_to_name          AS ShipToName,
        o.ship_to_city          AS ShipToCity,
        o.ship_to_country       AS ShipToCountry,
        o.total_net             AS TotalNet,
        o.currency              AS Currency,
        o.line_count            AS LineCount,
        o.quantity_total        AS QuantityTotal,
        o.first_ship_date       AS FirstShipDate,
        o.eta_date              AS EtaDate,
        o.last_synced_utc       AS LastSyncedUtc,
        o.last_changed_utc      AS LastChangedUtc,
        (SELECT count(*)::int FROM tdsynnex_ticket_orders l
           JOIN tickets t ON t.id = l.ticket_id AND t.is_deleted = FALSE
          WHERE l.order_id = o.id) AS LinkedTicketCount
        """;

    // ---- sync side ----------------------------------------------------

    public async Task<IReadOnlyDictionary<string, TdSynnexStoredOrder>> GetStoredAsync(
        IReadOnlyCollection<string> orderNumbers, CancellationToken ct)
    {
        var map = new Dictionary<string, TdSynnexStoredOrder>(StringComparer.Ordinal);
        if (orderNumbers.Count == 0) return map;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<StoredRow>(new CommandDefinition("""
            SELECT id AS Id, order_number AS OrderNumber, status AS Status,
                   lines::text AS LinesJson, snapshot_hash AS SnapshotHash
              FROM tdsynnex_orders
             WHERE order_number = ANY(@numbers)
            """, new { numbers = orderNumbers.ToArray() }, cancellationToken: ct));

        foreach (var r in rows)
        {
            var lines = DeserializeLines(r.LinesJson);
            map[r.OrderNumber] = new TdSynnexStoredOrder(r.Id,
                new TdSynnexOrder { OrderNumber = r.OrderNumber, Status = r.Status, Lines = lines },
                r.SnapshotHash);
        }
        return map;
    }

    public async Task<Guid> UpsertAsync(
        TdSynnexOrder o, TdSynnexOrderDerived d, bool changed, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO tdsynnex_orders (
                order_number, customer_order_number, end_user_order_number, order_type,
                status, status_detail, created_by, order_created_utc, order_modified_utc,
                ship_to_name, ship_to_contact, ship_to_city, ship_to_country,
                total_net, currency, line_count, quantity_total, first_ship_date, eta_date,
                lines, ship_to, payment, snapshot_hash, search_text,
                first_seen_utc, last_synced_utc, last_changed_utc)
            VALUES (
                @OrderNumber, @CustomerOrderNumber, @EndUserOrderNumber, @OrderType,
                @Status, @StatusDetail, @CreatedBy, @CreatedUtc, @ModifiedUtc,
                @ShipToName, @ShipToContact, @ShipToCity, @ShipToCountry,
                @TotalNet, @Currency, @LineCount, @QuantityTotal, @FirstShipDate::date, @EtaDate::date,
                @Lines::jsonb, @ShipTo::jsonb, @Payment::jsonb, @Hash, @SearchText,
                @Now, @Now, NULL)
            ON CONFLICT (order_number) DO UPDATE SET
                customer_order_number = EXCLUDED.customer_order_number,
                end_user_order_number = EXCLUDED.end_user_order_number,
                order_type            = EXCLUDED.order_type,
                status                = EXCLUDED.status,
                status_detail         = EXCLUDED.status_detail,
                created_by            = EXCLUDED.created_by,
                order_created_utc     = EXCLUDED.order_created_utc,
                order_modified_utc    = EXCLUDED.order_modified_utc,
                ship_to_name          = EXCLUDED.ship_to_name,
                ship_to_contact       = EXCLUDED.ship_to_contact,
                ship_to_city          = EXCLUDED.ship_to_city,
                ship_to_country       = EXCLUDED.ship_to_country,
                total_net             = EXCLUDED.total_net,
                currency              = EXCLUDED.currency,
                line_count            = EXCLUDED.line_count,
                quantity_total        = EXCLUDED.quantity_total,
                first_ship_date       = EXCLUDED.first_ship_date,
                eta_date              = EXCLUDED.eta_date,
                lines                 = EXCLUDED.lines,
                ship_to               = EXCLUDED.ship_to,
                payment               = EXCLUDED.payment,
                snapshot_hash         = EXCLUDED.snapshot_hash,
                search_text           = EXCLUDED.search_text,
                last_synced_utc       = EXCLUDED.last_synced_utc,
                last_changed_utc      = CASE WHEN @Changed THEN @Now ELSE tdsynnex_orders.last_changed_utc END
            RETURNING id
            """, new
        {
            o.OrderNumber,
            o.CustomerOrderNumber,
            o.EndUserOrderNumber,
            o.OrderType,
            o.Status,
            o.StatusDetail,
            o.CreatedBy,
            o.CreatedUtc,
            o.ModifiedUtc,
            ShipToName = o.ShipTo?.CompanyName,
            ShipToContact = o.ShipTo?.ContactName,
            ShipToCity = o.ShipTo?.City,
            ShipToCountry = o.ShipTo?.Country,
            o.TotalNet,
            Currency = o.Currency ?? o.Payment?.Currency,
            d.LineCount,
            d.QuantityTotal,
            FirstShipDate = AsDate(d.FirstShipDate),
            EtaDate = AsDate(d.EtaDate),
            Lines = JsonSerializer.Serialize(o.Lines, Json),
            ShipTo = o.ShipTo is null ? null : JsonSerializer.Serialize(o.ShipTo, Json),
            Payment = o.Payment is null ? null : JsonSerializer.Serialize(o.Payment, Json),
            Hash = d.SnapshotHash,
            d.SearchText,
            Now = nowUtc,
            Changed = changed,
        }, cancellationToken: ct));
    }

    public async Task AddChangeAsync(
        Guid orderId, DateTime nowUtc, string summary, IReadOnlyList<TdSynnexOrderChange> changes, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tdsynnex_order_changes (order_id, detected_utc, summary, changes)
            VALUES (@orderId, @nowUtc, @summary, @changes::jsonb)
            """, new
        {
            orderId,
            nowUtc,
            summary = summary.Length <= 4000 ? summary : summary[..4000],
            changes = JsonSerializer.Serialize(changes, Json),
        }, cancellationToken: ct));
    }

    public async Task<int> PurgeChangesAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tdsynnex_order_changes WHERE detected_utc < @cutoffUtc",
            new { cutoffUtc }, cancellationToken: ct));
    }

    public async Task<TdSynnexSyncStateRow?> GetSyncStateAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TdSynnexSyncStateRow>(new CommandDefinition("""
            SELECT s.delta_cursor_utc        AS DeltaCursorUtc,
                   s.last_active_refresh_utc AS LastActiveRefreshUtc,
                   s.last_checked_utc        AS LastCheckedUtc,
                   s.last_success_utc        AS LastSuccessUtc,
                   s.last_status             AS LastStatus,
                   s.last_error              AS LastError,
                   s.last_seen               AS LastSeen,
                   s.last_upserted           AS LastUpserted,
                   s.last_changed            AS LastChanged,
                   s.duration_ms             AS DurationMs,
                   (SELECT count(*)::int FROM tdsynnex_orders) AS OrderCount
              FROM tdsynnex_sync_state s
             WHERE s.id = 1
            """, cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(TdSynnexSyncStateRow s, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tdsynnex_sync_state (id, delta_cursor_utc, last_active_refresh_utc, last_checked_utc,
                last_success_utc, last_status, last_error, last_seen, last_upserted, last_changed, duration_ms)
            VALUES (1, @DeltaCursorUtc, @LastActiveRefreshUtc, @LastCheckedUtc,
                @LastSuccessUtc, @LastStatus, @LastError, @LastSeen, @LastUpserted, @LastChanged, @DurationMs)
            ON CONFLICT (id) DO UPDATE SET
                delta_cursor_utc        = EXCLUDED.delta_cursor_utc,
                last_active_refresh_utc = EXCLUDED.last_active_refresh_utc,
                last_checked_utc        = EXCLUDED.last_checked_utc,
                last_success_utc        = EXCLUDED.last_success_utc,
                last_status             = EXCLUDED.last_status,
                last_error              = EXCLUDED.last_error,
                last_seen               = EXCLUDED.last_seen,
                last_upserted           = EXCLUDED.last_upserted,
                last_changed            = EXCLUDED.last_changed,
                duration_ms             = EXCLUDED.duration_ms
            """, s, cancellationToken: ct));
    }

    // ---- dashboard side ------------------------------------------------

    private static string BucketPredicate(string bucket) => bucket switch
    {
        TdSynnexOrderBucket.Active => "o.status IN ('OPEN','INVESTIGATION','SHIPPING')",
        TdSynnexOrderBucket.Late => "o.status IN ('OPEN','INVESTIGATION','SHIPPING') AND o.first_ship_date < @today::date",
        TdSynnexOrderBucket.Due => "o.status IN ('OPEN','INVESTIGATION','SHIPPING') AND o.first_ship_date >= @today::date AND o.first_ship_date < (@today::date + 7)",
        TdSynnexOrderBucket.Open => "o.status = 'OPEN'",
        TdSynnexOrderBucket.Investigation => "o.status = 'INVESTIGATION'",
        TdSynnexOrderBucket.Shipping => "o.status = 'SHIPPING'",
        TdSynnexOrderBucket.Completed => "o.status = 'COMPLETED'",
        TdSynnexOrderBucket.Rejected => "o.status = 'REJECTED'",
        _ => "TRUE",
    };

    private static string OrderBy(string? sort, string? dir)
    {
        var desc = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        var d = desc ? "DESC NULLS LAST" : "ASC NULLS LAST";
        return sort switch
        {
            "eta" => $"o.first_ship_date {d}, o.id",
            "status" => $"o.status {d}, o.order_created_utc DESC NULLS LAST, o.id",
            "total" => $"o.total_net {d}, o.id",
            "orderNumber" => $"o.order_number {d}",
            "customerPo" => $"o.customer_order_number {d}, o.id",
            "shipTo" => $"o.ship_to_name {d}, o.id",
            "changed" => $"o.last_changed_utc {d}, o.id",
            _ => $"o.order_created_utc {d}, o.id",
        };
    }

    public async Task<TdSynnexOrderListResult> ListAsync(
        string? search, string bucket, DateOnly today, string? sort, string? dir, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = (search ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length > 200) q = q[..200];

        // Both fragments come from fixed switches — never user input.
        var where = BucketPredicate(TdSynnexOrderBucket.Known.Contains(bucket) ? bucket : TdSynnexOrderBucket.All);
        var orderBy = OrderBy(sort, dir);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var args = new { like = "%" + EscapeLike(q) + "%", hasQ = q.Length > 0, today = AsDate(today), limit = pageSize, offset = (page - 1) * pageSize };
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition($"""
            SELECT count(*)::int FROM tdsynnex_orders o
             WHERE {where} AND (NOT @hasQ OR o.search_text LIKE @like)
            """, args, cancellationToken: ct));
        var rows = (await conn.QueryAsync<TdSynnexOrderRow>(new CommandDefinition($"""
            SELECT {HeaderColumns}
              FROM tdsynnex_orders o
             WHERE {where} AND (NOT @hasQ OR o.search_text LIKE @like)
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset
            """, args, cancellationToken: ct))).ToList();
        return new TdSynnexOrderListResult(rows, total, page, pageSize);
    }

    public async Task<TdSynnexOrderStats> GetStatsAsync(DateOnly today, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var r = await conn.QuerySingleAsync<StatsRow>(new CommandDefinition("""
            SELECT count(*) FILTER (WHERE status = 'OPEN')::int          AS Open,
                   count(*) FILTER (WHERE status = 'INVESTIGATION')::int AS Investigation,
                   count(*) FILTER (WHERE status = 'SHIPPING')::int      AS Shipping,
                   count(*) FILTER (WHERE status IN ('OPEN','INVESTIGATION','SHIPPING')
                                      AND first_ship_date < @today::date)::int AS Late,
                   count(*) FILTER (WHERE status IN ('OPEN','INVESTIGATION','SHIPPING')
                                      AND first_ship_date >= @today::date
                                      AND first_ship_date < @weekEnd::date)::int AS DueThisWeek,
                   -- TD SYNNEX's own modification date: an order that was
                   -- already completed when first synced counts too.
                   count(*) FILTER (WHERE status = 'COMPLETED'
                                      AND COALESCE(order_modified_utc, last_changed_utc) >= @since)::int AS CompletedLast30Days,
                   COALESCE(sum(total_net) FILTER (WHERE status IN ('OPEN','INVESTIGATION','SHIPPING')), 0) AS OpenValue,
                   (SELECT currency FROM tdsynnex_orders WHERE currency IS NOT NULL
                     GROUP BY currency ORDER BY count(*) DESC LIMIT 1) AS Currency
              FROM tdsynnex_orders
            """, new
        {
            today = AsDate(today),
            weekEnd = AsDate(today.AddDays(7)),
            since = today.AddDays(-30).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        }, cancellationToken: ct));
        return new TdSynnexOrderStats(r.Open, r.Investigation, r.Shipping, r.Late, r.DueThisWeek,
            r.CompletedLast30Days, r.OpenValue, r.Currency);
    }

    public async Task<IReadOnlyList<TdSynnexOrderRow>> PickAsync(string? query, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 25);
        var q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length > 200) q = q[..200];
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TdSynnexOrderRow>(new CommandDefinition($"""
            SELECT {HeaderColumns}
              FROM tdsynnex_orders o
             WHERE (@q = '' OR o.search_text LIKE @like)
             ORDER BY (lower(o.order_number) = @q OR lower(o.customer_order_number) = @q) DESC,
                      (o.status IN ('OPEN','INVESTIGATION','SHIPPING')) DESC,
                      o.order_created_utc DESC NULLS LAST, o.id
             LIMIT @limit
            """, new { q, like = "%" + EscapeLike(q) + "%", limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<TdSynnexOrderDetail?> GetDetailAsync(Guid id, int changeLimit, CancellationToken ct)
    {
        var list = await LoadDetailsAsync("o.id = @id", new { id }, changeLimit, ct);
        return list.Count == 0 ? null : list[0];
    }

    public Task<IReadOnlyList<TdSynnexOrderDetail>> ListForTicketAsync(Guid ticketId, int changeLimit, CancellationToken ct) =>
        LoadDetailsAsync(
            "o.id IN (SELECT order_id FROM tdsynnex_ticket_orders WHERE ticket_id = @ticketId)",
            new { ticketId }, changeLimit, ct);

    private async Task<IReadOnlyList<TdSynnexOrderDetail>> LoadDetailsAsync(
        string where, object args, int changeLimit, CancellationToken ct)
    {
        changeLimit = Math.Clamp(changeLimit, 0, 200);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        // `where` is one of two constant fragments above — never user input.
        var rows = (await conn.QueryAsync<DetailRow>(new CommandDefinition($"""
            SELECT {HeaderColumns},
                   o.lines::text   AS LinesJson,
                   o.ship_to::text AS ShipToJson,
                   o.payment::text AS PaymentJson
              FROM tdsynnex_orders o
             WHERE {where}
             ORDER BY o.order_created_utc DESC NULLS LAST, o.id
             LIMIT 50
            """, args, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return Array.Empty<TdSynnexOrderDetail>();

        var changes = changeLimit == 0
            ? new List<ChangeJoinRow>()
            : (await conn.QueryAsync<ChangeJoinRow>(new CommandDefinition("""
                SELECT c.order_id AS OrderId, c.id AS Id, c.detected_utc AS DetectedUtc,
                       c.summary AS Summary, c.changes::text AS ChangesJson
                  FROM unnest(@ids::uuid[]) AS i(order_id)
                  CROSS JOIN LATERAL (
                      SELECT * FROM tdsynnex_order_changes x
                       WHERE x.order_id = i.order_id
                       ORDER BY x.detected_utc DESC, x.id DESC
                       LIMIT @changeLimit) c
                """, new { ids = rows.Select(r => r.Id).ToArray(), changeLimit }, cancellationToken: ct))).ToList();
        var byOrder = changes.ToLookup(c => c.OrderId);

        return rows.Select(r => new TdSynnexOrderDetail(
            r,
            DeserializeLines(r.LinesJson),
            Deserialize<TdSynnexShipTo>(r.ShipToJson),
            Deserialize<TdSynnexPayment>(r.PaymentJson),
            byOrder[r.Id].Select(c => new TdSynnexOrderChangeRow
            {
                Id = c.Id, DetectedUtc = c.DetectedUtc, Summary = c.Summary, ChangesJson = c.ChangesJson,
            }).ToList())).ToList();
    }

    // ---- ticket links ---------------------------------------------------

    public async Task<bool> LinkAsync(Guid ticketId, Guid orderId, Guid? userId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tdsynnex_ticket_orders (ticket_id, order_id, linked_by_user_id)
            VALUES (@ticketId, @orderId, @userId)
            ON CONFLICT (ticket_id, order_id) DO NOTHING
            """, new { ticketId, orderId, userId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> UnlinkAsync(Guid ticketId, Guid orderId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tdsynnex_ticket_orders WHERE ticket_id = @ticketId AND order_id = @orderId",
            new { ticketId, orderId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<int> CountLinksForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM tdsynnex_ticket_orders WHERE ticket_id = @ticketId",
            new { ticketId }, cancellationToken: ct));
    }

    public async Task<TdSynnexLinkedTickets> GetLinkedTicketsAsync(Guid orderId, QueueAccessScope scope, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var p = scope.Parameters(new { orderId });
        var visible = (await conn.QueryAsync<TdSynnexLinkedTicketRow>(new CommandDefinition($"""
            SELECT t.id AS TicketId, t.number AS Number, t.subject AS Subject,
                   s.name AS StatusName, s.color AS StatusColor
              FROM tdsynnex_ticket_orders l
              JOIN tickets t  ON t.id = l.ticket_id AND t.is_deleted = FALSE
              JOIN statuses s ON s.id = t.status_id
             WHERE l.order_id = @orderId
               AND {scope.SqlPredicate("t.queue_id")}
             ORDER BY t.number DESC
             LIMIT 100
            """, p, cancellationToken: ct))).ToList();
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*)::int
              FROM tdsynnex_ticket_orders l
              JOIN tickets t ON t.id = l.ticket_id AND t.is_deleted = FALSE
             WHERE l.order_id = @orderId
            """, new { orderId }, cancellationToken: ct));
        return new TdSynnexLinkedTickets(visible, Math.Max(0, total - visible.Count));
    }

    public async Task<IReadOnlyList<Guid>> GetLinkedTicketIdsAsync(Guid orderId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var ids = await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT l.ticket_id
              FROM tdsynnex_ticket_orders l
              JOIN tickets t ON t.id = l.ticket_id AND t.is_deleted = FALSE
             WHERE l.order_id = @orderId
            """, new { orderId }, cancellationToken: ct));
        return ids.ToList();
    }

    // ---- helpers --------------------------------------------------------

    public static IReadOnlyList<TdSynnexOrderLine> DeserializeLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TdSynnexOrderLine>();
        try { return JsonSerializer.Deserialize<List<TdSynnexOrderLine>>(json, Json) ?? new List<TdSynnexOrderLine>(); }
        catch (JsonException) { return Array.Empty<TdSynnexOrderLine>(); }
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static DateTime? AsDate(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class StoredRow
    {
        public Guid Id { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? LinesJson { get; set; }
        public byte[]? SnapshotHash { get; set; }
    }

    private sealed class DetailRow : TdSynnexOrderRow
    {
        public string? LinesJson { get; set; }
        public string? ShipToJson { get; set; }
        public string? PaymentJson { get; set; }
    }

    private sealed class ChangeJoinRow
    {
        public Guid OrderId { get; set; }
        public long Id { get; set; }
        public DateTime DetectedUtc { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string ChangesJson { get; set; } = "[]";
    }

    private sealed class StatsRow
    {
        public int Open { get; set; }
        public int Investigation { get; set; }
        public int Shipping { get; set; }
        public int Late { get; set; }
        public int DueThisWeek { get; set; }
        public int CompletedLast30Days { get; set; }
        public decimal OpenValue { get; set; }
        public string? Currency { get; set; }
    }
}
