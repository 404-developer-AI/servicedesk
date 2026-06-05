using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Singleton sync-state row for the Orders mirror. Sealed class with get/set +
/// AS-PascalCase aliases per the project's Dapper conventions.
public sealed class AdsolutOrderSyncState
{
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastDeltaSyncUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int OrdersSeen { get; set; }
    public int OrdersUpserted { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    // Supplier-orders (bestellingen) pass — independent delta cursor in the
    // same singleton row (same worker tick + toggle).
    public DateTime? SupplierLastDeltaSyncUtc { get; set; }
    public int SupplierOrdersSeen { get; set; }
    public int SupplierOrdersUpserted { get; set; }
}

/// One supplier-order ("bestelling") line for the order-detail "Bestellingen"
/// block. Sealed class + AS-PascalCase per the Dapper conventions.
public sealed class AdsolutSupplierOrderLineRow
{
    public Guid Id { get; set; }
    public int? BlDocNr { get; set; }
    public string? BlBookCode { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierCode { get; set; }
    public DateTime? SupplierOrderDate { get; set; }
    /// Resolved warehouse name (Stock) — COALESCE(mirror name, line code).
    public string? WarehouseName { get; set; }
    /// Resolved warehouse-location name (Location) — COALESCE(mirror name, line code).
    public string? WarehouseLocationName { get; set; }
    public int? LineNr { get; set; }
    public string? ProductCode { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Delivered { get; set; }
    public string? UnitCode { get; set; }
    public decimal? GrossUnitPrice { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Discount1 { get; set; }
    public string? StatusCode { get; set; }
    public int? LinkedOrderDocNr { get; set; }
}

/// One distinct order state in the mirror, with how many orders carry it —
/// powers the dynamic status-filter checkboxes (display-only filter).
public sealed class AdsolutOrderStatusOption
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Count { get; set; }
}

/// Header row for the Orders overview + detail + ::-picker.
public sealed class AdsolutOrderRow
{
    public Guid Id { get; set; }
    public int? DocNr { get; set; }
    public string? BookCode { get; set; }
    public string? KluwerRef { get; set; }
    public Guid? CustomerAdsolutId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerCode { get; set; }
    public string? StateCode { get; set; }
    public string? StateDescription { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? RequestedDeliveryDate { get; set; }
    public DateTime? ConfirmedDeliveryDate { get; set; }
    public string? Remark { get; set; }
    public string? InternalMemo { get; set; }
    public string? RepresentativeName { get; set; }
    public decimal TotalExclVat { get; set; }
    public decimal TotalInclVat { get; set; }
    public string? CurrencyIso { get; set; }
    public long? TicketNumber { get; set; }
    public DateTime? AdsolutCreatedUtc { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime SyncedUtc { get; set; }
}

public sealed class AdsolutOrderLineRow
{
    public Guid Id { get; set; }
    public int? LineNr { get; set; }
    public string? ProductCode { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public string? UnitCode { get; set; }
    public string? UnitDescription { get; set; }
    public decimal? Delivered { get; set; }
    public decimal? GrossUnitPrice { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Discount1 { get; set; }
    public decimal? Discount2 { get; set; }
    public decimal? PriceExclVat { get; set; }
    public decimal? PriceInclVat { get; set; }
    public string? VatCode { get; set; }
    public string? VatDescription { get; set; }
    public string? StateCode { get; set; }
    public string? StateDescription { get; set; }
}

public sealed record AdsolutOrderDetail(
    AdsolutOrderRow Header,
    IReadOnlyList<AdsolutOrderLineRow> Lines);

public sealed record AdsolutOrderListResult(
    IReadOnlyList<AdsolutOrderRow> Items,
    int Total,
    int Page,
    int PageSize);

public interface IAdsolutOrderRepository
{
    /// Upsert one order header and replace its line-set wholesale, in a single
    /// transaction. Header totals come straight from the API (no compute).
    Task UpsertAsync(AdsolutOrder order, CancellationToken ct = default);

    Task<AdsolutOrderSyncState?> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(AdsolutOrderSyncState state, CancellationToken ct = default);

    /// Distinct states seen in the mirror (for the display status-filter UI).
    Task<IReadOnlyList<AdsolutOrderStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default);

    Task<int> GetCountAsync(CancellationToken ct = default);

    /// Paged overview list. <paramref name="search"/> matches customer name,
    /// remark, document number and kluwer ref. <paramref name="sort"/> is a
    /// whitelisted key (date/doc/customer/status/total/delivery);
    /// <paramref name="dir"/> is "asc"/"desc". <paramref name="statusFilter"/>
    /// is the admin's DISPLAY filter — when non-empty, only those state codes
    /// are returned (the mirror still holds every status).
    Task<AdsolutOrderListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        IReadOnlyCollection<string> statusFilter, CancellationToken ct = default);

    /// One order with its detail lines (overview detail + ::-pill view).
    Task<AdsolutOrderDetail?> GetDetailAsync(Guid id, CancellationToken ct = default);

    /// Lightweight rows for the ticket editor's "::" order picker. Honors the
    /// display status filter and matches doc number, kluwer ref, customer name
    /// and remark. Capped at <paramref name="limit"/>.
    Task<IReadOnlyList<AdsolutOrderRow>> SearchForPickerAsync(
        string? query, IReadOnlyCollection<string> statusFilter, int limit, CancellationToken ct = default);

    // ---- ticket ↔ order links ------------------------------------------
    Task<bool> LinkToTicketAsync(Guid ticketId, Guid orderId, Guid? userId, CancellationToken ct = default);
    Task UnlinkFromTicketAsync(Guid ticketId, Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<AdsolutOrderRow>> ListForTicketAsync(Guid ticketId, CancellationToken ct = default);

    // ---- supplier orders (bestellingen) --------------------------------
    /// Upsert one supplier order: replace its line-set wholesale (grouped by
    /// supplier_order_id), denormalising supplier + date onto each line.
    Task UpsertSupplierOrderAsync(AdsolutSupplierOrder order, CancellationToken ct = default);

    /// Supplier-order lines linked to a sales order (join on linked_order_id =
    /// the order id), for the order-detail "Bestellingen" block.
    Task<IReadOnlyList<AdsolutSupplierOrderLineRow>> GetSupplierLinesForOrderAsync(Guid orderId, CancellationToken ct = default);

    /// Distinct supplier-order statuses seen in the mirror (for the colour UI).
    Task<IReadOnlyList<AdsolutOrderStatusOption>> GetSupplierStatusOptionsAsync(CancellationToken ct = default);

    // ---- warehouses (Stock / Location name resolution) -----------------
    /// Replace the Warehouses reference mirror (+ locations) wholesale from a
    /// full Warehouses pull. Upserts every supplied row, then deletes mirror
    /// rows whose id is no longer present (handles upstream removals). No-op on
    /// an empty list to avoid wiping the mirror on a transient empty page.
    Task UpsertWarehousesAsync(IReadOnlyList<AdsolutWarehouse> warehouses, CancellationToken ct = default);
}

public sealed class AdsolutOrderRepository : IAdsolutOrderRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutOrderRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(AdsolutOrder o, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_orders (
                id, doc_nr, book_code, kluwer_ref, customer_adsolut_id, customer_code, customer_name,
                state_id, state_code, state_description, order_date,
                requested_delivery_date, confirmed_delivery_date,
                remark, internal_memo, representative_code, representative_name,
                currency_iso, total_excl_vat, total_incl_vat, ticket_number,
                adsolut_created_utc, adsolut_last_modified, synced_utc
            ) VALUES (
                @Id, @DocNr, @BookCode, @KluwerRef, @CustomerAdsolutId, @CustomerCode, @CustomerName,
                @StateId, @StateCode, @StateDescription, @OrderDate,
                @RequestedDeliveryDate, @ConfirmedDeliveryDate,
                @Remark, @InternalMemo, @RepresentativeCode, @RepresentativeName,
                @CurrencyIso, @TotalExclVat, @TotalInclVat, @TicketNumber,
                @AdsolutCreatedUtc, @AdsolutLastModified, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                doc_nr                  = EXCLUDED.doc_nr,
                book_code               = EXCLUDED.book_code,
                kluwer_ref              = EXCLUDED.kluwer_ref,
                customer_adsolut_id     = EXCLUDED.customer_adsolut_id,
                customer_code           = EXCLUDED.customer_code,
                customer_name           = EXCLUDED.customer_name,
                state_id                = EXCLUDED.state_id,
                state_code              = EXCLUDED.state_code,
                state_description       = EXCLUDED.state_description,
                order_date              = EXCLUDED.order_date,
                requested_delivery_date = EXCLUDED.requested_delivery_date,
                confirmed_delivery_date = EXCLUDED.confirmed_delivery_date,
                remark                  = EXCLUDED.remark,
                internal_memo           = EXCLUDED.internal_memo,
                representative_code     = EXCLUDED.representative_code,
                representative_name     = EXCLUDED.representative_name,
                currency_iso            = EXCLUDED.currency_iso,
                total_excl_vat          = EXCLUDED.total_excl_vat,
                total_incl_vat          = EXCLUDED.total_incl_vat,
                ticket_number           = EXCLUDED.ticket_number,
                adsolut_created_utc     = EXCLUDED.adsolut_created_utc,
                adsolut_last_modified   = EXCLUDED.adsolut_last_modified,
                synced_utc              = now()
            """,
            new
            {
                o.Id,
                o.DocNr,
                o.BookCode,
                o.KluwerRef,
                o.CustomerAdsolutId,
                o.CustomerCode,
                o.CustomerName,
                o.StateId,
                o.StateCode,
                o.StateDescription,
                OrderDate = o.OrderDate?.UtcDateTime,
                RequestedDeliveryDate = o.RequestedDeliveryDate?.UtcDateTime,
                ConfirmedDeliveryDate = o.ConfirmedDeliveryDate?.UtcDateTime,
                o.Remark,
                o.InternalMemo,
                o.RepresentativeCode,
                o.RepresentativeName,
                o.CurrencyIso,
                o.TotalExclVat,
                o.TotalInclVat,
                TicketNumber = ParseTicketNumber(o.Remark),
                AdsolutCreatedUtc = o.AdsolutCreatedUtc?.UtcDateTime,
                AdsolutLastModified = o.AdsolutLastModified?.UtcDateTime,
            },
            transaction: tx,
            cancellationToken: ct));

        // Replace child lines wholesale — simplest correct strategy when an
        // order's lines can be added/removed upstream between syncs.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_order_lines WHERE order_id = @Id",
            new { o.Id }, transaction: tx, cancellationToken: ct));

        if (o.Lines.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adsolut_order_lines (
                    id, order_id, line_nr, product_id, product_code, description,
                    quantity, unit_code, unit_description, delivered,
                    gross_unit_price, unit_price, discount1, discount2,
                    price_excl_vat, price_incl_vat, vat_code, vat_description,
                    state_code, state_description,
                    requested_delivery_date, confirmed_delivery_date
                ) VALUES (
                    @Id, @OrderId, @LineNr, @ProductId, @ProductCode, @Description,
                    @Quantity, @UnitCode, @UnitDescription, @Delivered,
                    @GrossUnitPrice, @UnitPrice, @Discount1, @Discount2,
                    @PriceExclVat, @PriceInclVat, @VatCode, @VatDescription,
                    @StateCode, @StateDescription,
                    @RequestedDeliveryDate, @ConfirmedDeliveryDate
                )
                """,
                o.Lines.Select(l => new
                {
                    l.Id,
                    OrderId = o.Id,
                    l.LineNr,
                    l.ProductId,
                    l.ProductCode,
                    l.Description,
                    l.Quantity,
                    l.UnitCode,
                    l.UnitDescription,
                    l.Delivered,
                    l.GrossUnitPrice,
                    l.UnitPrice,
                    l.Discount1,
                    l.Discount2,
                    l.PriceExclVat,
                    l.PriceInclVat,
                    l.VatCode,
                    l.VatDescription,
                    l.StateCode,
                    l.StateDescription,
                    RequestedDeliveryDate = l.RequestedDeliveryDate?.UtcDateTime,
                    ConfirmedDeliveryDate = l.ConfirmedDeliveryDate?.UtcDateTime,
                }),
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<AdsolutOrderSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutOrderSyncState>(new CommandDefinition(
            """
            SELECT
                last_full_sync_utc           AS LastFullSyncUtc,
                last_delta_sync_utc          AS LastDeltaSyncUtc,
                last_error                   AS LastError,
                last_error_utc               AS LastErrorUtc,
                orders_seen                  AS OrdersSeen,
                orders_upserted              AS OrdersUpserted,
                updated_utc                  AS UpdatedUtc,
                supplier_last_delta_sync_utc AS SupplierLastDeltaSyncUtc,
                supplier_orders_seen         AS SupplierOrdersSeen,
                supplier_orders_upserted     AS SupplierOrdersUpserted
            FROM adsolut_order_sync_state
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(AdsolutOrderSyncState state, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_order_sync_state (
                id, last_full_sync_utc, last_delta_sync_utc, last_error, last_error_utc,
                orders_seen, orders_upserted, updated_utc,
                supplier_last_delta_sync_utc, supplier_orders_seen, supplier_orders_upserted
            ) VALUES (
                1, @LastFullSyncUtc, @LastDeltaSyncUtc, @LastError, @LastErrorUtc,
                @OrdersSeen, @OrdersUpserted, now(),
                @SupplierLastDeltaSyncUtc, @SupplierOrdersSeen, @SupplierOrdersUpserted
            )
            ON CONFLICT (id) DO UPDATE SET
                last_full_sync_utc           = EXCLUDED.last_full_sync_utc,
                last_delta_sync_utc          = EXCLUDED.last_delta_sync_utc,
                last_error                   = EXCLUDED.last_error,
                last_error_utc               = EXCLUDED.last_error_utc,
                orders_seen                  = EXCLUDED.orders_seen,
                orders_upserted              = EXCLUDED.orders_upserted,
                updated_utc                  = now(),
                supplier_last_delta_sync_utc = EXCLUDED.supplier_last_delta_sync_utc,
                supplier_orders_seen         = EXCLUDED.supplier_orders_seen,
                supplier_orders_upserted     = EXCLUDED.supplier_orders_upserted
            """,
            new
            {
                state.LastFullSyncUtc,
                state.LastDeltaSyncUtc,
                state.LastError,
                state.LastErrorUtc,
                state.OrdersSeen,
                state.OrdersUpserted,
                state.SupplierLastDeltaSyncUtc,
                state.SupplierOrdersSeen,
                state.SupplierOrdersUpserted,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdsolutOrderStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutOrderStatusOption>(new CommandDefinition(
            """
            SELECT
                state_code             AS Code,
                MAX(state_description) AS Description,
                COUNT(*)::int          AS Count
            FROM adsolut_orders
            WHERE state_code IS NOT NULL
            GROUP BY state_code
            ORDER BY state_code
            """,
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM adsolut_orders",
            cancellationToken: ct));
    }

    private const string HeaderColumns = """
        id                      AS Id,
        doc_nr                  AS DocNr,
        book_code               AS BookCode,
        kluwer_ref              AS KluwerRef,
        customer_adsolut_id     AS CustomerAdsolutId,
        customer_name           AS CustomerName,
        customer_code           AS CustomerCode,
        state_code              AS StateCode,
        state_description       AS StateDescription,
        order_date              AS OrderDate,
        requested_delivery_date AS RequestedDeliveryDate,
        confirmed_delivery_date AS ConfirmedDeliveryDate,
        remark                  AS Remark,
        internal_memo           AS InternalMemo,
        representative_name     AS RepresentativeName,
        total_excl_vat          AS TotalExclVat,
        total_incl_vat          AS TotalInclVat,
        currency_iso            AS CurrencyIso,
        ticket_number           AS TicketNumber,
        adsolut_created_utc     AS AdsolutCreatedUtc,
        adsolut_last_modified   AS AdsolutLastModified,
        synced_utc              AS SyncedUtc
        """;

    public async Task<AdsolutOrderListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        IReadOnlyCollection<string> statusFilter, CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (safePage - 1) * safePageSize;
        var term = (search ?? string.Empty).Trim();
        var hasSearch = term.Length > 0;
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        long? docProbe = long.TryParse(term, out var dn) ? dn : null;
        var statuses = statusFilter.Count == 0 ? null : statusFilter.ToArray();

        var sortExpr = (sort ?? "date").Trim().ToLowerInvariant() switch
        {
            "doc" => "doc_nr",
            "customer" => "customer_name",
            "status" => "state_code",
            "total" => "total_excl_vat",
            "delivery" => "requested_delivery_date",
            _ => "order_date",
        };
        var dirSql = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        const string whereClause = """
            WHERE (@Statuses::text[] IS NULL OR state_code = ANY(@Statuses::text[]))
              AND (@HasSearch = FALSE
                   OR customer_name ILIKE @Like
                   OR remark        ILIKE @Like
                   OR kluwer_ref    ILIKE @Like
                   OR (@DocProbe IS NOT NULL AND doc_nr = @DocProbe))
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*)::int FROM adsolut_orders {whereClause}",
            new { Statuses = statuses, HasSearch = hasSearch, Like = like, DocProbe = docProbe },
            cancellationToken: ct));

        var items = await conn.QueryAsync<AdsolutOrderRow>(new CommandDefinition(
            $"""
            SELECT {HeaderColumns}
            FROM adsolut_orders
            {whereClause}
            ORDER BY {sortExpr} {dirSql} NULLS LAST, doc_nr DESC NULLS LAST
            LIMIT @Limit OFFSET @Offset
            """,
            new { Statuses = statuses, HasSearch = hasSearch, Like = like, DocProbe = docProbe, Limit = safePageSize, Offset = offset },
            cancellationToken: ct));

        return new AdsolutOrderListResult(items.ToList(), total, safePage, safePageSize);
    }

    public async Task<AdsolutOrderDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var header = await conn.QueryFirstOrDefaultAsync<AdsolutOrderRow>(new CommandDefinition(
            $"SELECT {HeaderColumns} FROM adsolut_orders WHERE id = @id",
            new { id }, cancellationToken: ct));
        if (header is null) return null;

        var lines = await conn.QueryAsync<AdsolutOrderLineRow>(new CommandDefinition(
            """
            SELECT id AS Id, line_nr AS LineNr, product_code AS ProductCode, description AS Description,
                   quantity AS Quantity, unit_code AS UnitCode, unit_description AS UnitDescription,
                   delivered AS Delivered, gross_unit_price AS GrossUnitPrice, unit_price AS UnitPrice,
                   discount1 AS Discount1, discount2 AS Discount2,
                   price_excl_vat AS PriceExclVat, price_incl_vat AS PriceInclVat,
                   vat_code AS VatCode, vat_description AS VatDescription,
                   state_code AS StateCode, state_description AS StateDescription
            FROM adsolut_order_lines
            WHERE order_id = @id
            ORDER BY line_nr NULLS LAST
            """,
            new { id }, cancellationToken: ct));

        return new AdsolutOrderDetail(header, lines.ToList());
    }

    public async Task<IReadOnlyList<AdsolutOrderRow>> SearchForPickerAsync(
        string? query, IReadOnlyCollection<string> statusFilter, int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);
        var term = (query ?? string.Empty).Trim();
        var hasSearch = term.Length > 0;
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        long? docProbe = long.TryParse(term, out var dn) ? dn : null;
        var statuses = statusFilter.Count == 0 ? null : statusFilter.ToArray();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync<AdsolutOrderRow>(new CommandDefinition(
            $"""
            SELECT {HeaderColumns}
            FROM adsolut_orders
            WHERE (@Statuses::text[] IS NULL OR state_code = ANY(@Statuses::text[]))
              AND (@HasSearch = FALSE
                   OR customer_name ILIKE @Like
                   OR remark        ILIKE @Like
                   OR kluwer_ref    ILIKE @Like
                   OR (@DocProbe IS NOT NULL AND doc_nr = @DocProbe))
            ORDER BY order_date DESC NULLS LAST, doc_nr DESC NULLS LAST
            LIMIT @Limit
            """,
            new { Statuses = statuses, HasSearch = hasSearch, Like = like, DocProbe = docProbe, Limit = safeLimit },
            cancellationToken: ct));
        return items.ToList();
    }

    public async Task<bool> LinkToTicketAsync(Guid ticketId, Guid orderId, Guid? userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_order_links (ticket_id, order_id, linked_by, linked_utc)
            VALUES (@ticketId, @orderId, @userId, now())
            ON CONFLICT (ticket_id, order_id) DO NOTHING
            """,
            new { ticketId, orderId, userId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task UnlinkFromTicketAsync(Guid ticketId, Guid orderId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ticket_order_links WHERE ticket_id = @ticketId AND order_id = @orderId",
            new { ticketId, orderId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdsolutOrderRow>> ListForTicketAsync(Guid ticketId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync<AdsolutOrderRow>(new CommandDefinition(
            $"""
            SELECT {HeaderColumns}
            FROM adsolut_orders o
            JOIN ticket_order_links l ON l.order_id = o.id
            WHERE l.ticket_id = @ticketId
            ORDER BY l.linked_utc DESC
            """,
            new { ticketId }, cancellationToken: ct));
        return items.ToList();
    }

    public async Task UpsertSupplierOrderAsync(AdsolutSupplierOrder o, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_supplier_order_lines WHERE supplier_order_id = @Id",
            new { o.Id }, transaction: tx, cancellationToken: ct));

        if (o.Lines.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adsolut_supplier_order_lines (
                    id, supplier_order_id, bl_doc_nr, bl_book_code,
                    supplier_name, supplier_code, supplier_order_date, header_state_code,
                    warehouse_id, warehouse_code, warehouse_location_id, warehouse_location_code,
                    line_nr, product_id, product_code, name, description,
                    quantity, delivered, unit_code, gross_unit_price, unit_price, discount1,
                    status_code, linked_order_id, linked_order_doc_nr, adsolut_last_modified, synced_utc
                ) VALUES (
                    @Id, @SupplierOrderId, @BlDocNr, @BlBookCode,
                    @SupplierName, @SupplierCode, @SupplierOrderDate, @HeaderStateCode,
                    @WarehouseId, @WarehouseCode, @WarehouseLocationId, @WarehouseLocationCode,
                    @LineNr, @ProductId, @ProductCode, @Name, @Description,
                    @Quantity, @Delivered, @UnitCode, @GrossUnitPrice, @UnitPrice, @Discount1,
                    @StatusCode, @LinkedOrderId, @LinkedOrderDocNr, @AdsolutLastModified, now()
                )
                """,
                o.Lines.Select(l => new
                {
                    l.Id,
                    SupplierOrderId = o.Id,
                    BlDocNr = o.DocNr,
                    BlBookCode = o.BookCode,
                    SupplierName = o.SupplierName,
                    SupplierCode = o.SupplierCode,
                    SupplierOrderDate = o.SupplierOrderDate?.UtcDateTime,
                    HeaderStateCode = o.StateCode,
                    l.WarehouseId,
                    l.WarehouseCode,
                    l.WarehouseLocationId,
                    l.WarehouseLocationCode,
                    l.LineNr,
                    l.ProductId,
                    l.ProductCode,
                    l.Name,
                    l.Description,
                    l.Quantity,
                    l.Delivered,
                    l.UnitCode,
                    l.GrossUnitPrice,
                    l.UnitPrice,
                    l.Discount1,
                    l.StatusCode,
                    l.LinkedOrderId,
                    l.LinkedOrderDocNr,
                    AdsolutLastModified = o.AdsolutLastModified?.UtcDateTime,
                }),
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AdsolutSupplierOrderLineRow>> GetSupplierLinesForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutSupplierOrderLineRow>(new CommandDefinition(
            """
            SELECT l.id AS Id, l.bl_doc_nr AS BlDocNr, l.bl_book_code AS BlBookCode,
                   l.supplier_name AS SupplierName, l.supplier_code AS SupplierCode,
                   l.supplier_order_date AS SupplierOrderDate,
                   -- Resolve warehouse/location code+id to a readable name; fall
                   -- back to the line's raw code when the mirror has no match yet.
                   COALESCE(w.name, l.warehouse_code)           AS WarehouseName,
                   COALESCE(wl.name, l.warehouse_location_code) AS WarehouseLocationName,
                   l.line_nr AS LineNr,
                   l.product_code AS ProductCode, l.name AS Name, l.description AS Description,
                   l.quantity AS Quantity, l.delivered AS Delivered, l.unit_code AS UnitCode,
                   l.gross_unit_price AS GrossUnitPrice, l.unit_price AS UnitPrice, l.discount1 AS Discount1,
                   l.status_code AS StatusCode, l.linked_order_doc_nr AS LinkedOrderDocNr
            FROM adsolut_supplier_order_lines l
            LEFT JOIN adsolut_warehouses w ON w.id = l.warehouse_id
            LEFT JOIN adsolut_warehouse_locations wl ON wl.id = l.warehouse_location_id
            WHERE l.linked_order_id = @orderId
            ORDER BY l.bl_doc_nr NULLS LAST, l.line_nr NULLS LAST
            """,
            new { orderId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AdsolutOrderStatusOption>> GetSupplierStatusOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutOrderStatusOption>(new CommandDefinition(
            """
            SELECT
                status_code   AS Code,
                NULL::text    AS Description,
                COUNT(*)::int AS Count
            FROM adsolut_supplier_order_lines
            WHERE status_code IS NOT NULL AND status_code <> ''
            GROUP BY status_code
            ORDER BY status_code
            """,
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpsertWarehousesAsync(IReadOnlyList<AdsolutWarehouse> warehouses, CancellationToken ct = default)
    {
        // Never wipe the mirror on a transient empty pull — leave the last-good
        // names in place so Stock/Location keep resolving.
        if (warehouses.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_warehouses (id, code, name, active, standard, synced_utc)
            VALUES (@Id, @Code, @Name, @Active, @Standard, now())
            ON CONFLICT (id) DO UPDATE SET
                code       = EXCLUDED.code,
                name       = EXCLUDED.name,
                active     = EXCLUDED.active,
                standard   = EXCLUDED.standard,
                synced_utc = now()
            """,
            warehouses.Select(w => new { w.Id, w.Code, w.Name, w.Active, w.Standard }),
            transaction: tx, cancellationToken: ct));

        var locations = warehouses
            .SelectMany(w => w.Locations.Select(l => new { l.Id, WarehouseId = w.Id, l.Name, l.IsDefault }))
            .ToList();
        if (locations.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adsolut_warehouse_locations (id, warehouse_id, name, is_default, synced_utc)
                VALUES (@Id, @WarehouseId, @Name, @IsDefault, now())
                ON CONFLICT (id) DO UPDATE SET
                    warehouse_id = EXCLUDED.warehouse_id,
                    name         = EXCLUDED.name,
                    is_default   = EXCLUDED.is_default,
                    synced_utc   = now()
                """,
                locations, transaction: tx, cancellationToken: ct));
        }

        // Delete rows that vanished upstream (id not in the current full pull).
        var warehouseIds = warehouses.Select(w => w.Id).ToArray();
        var locationIds = locations.Select(l => l.Id).ToArray();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_warehouses WHERE id <> ALL(@warehouseIds)",
            new { warehouseIds }, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_warehouse_locations WHERE id <> ALL(@locationIds)",
            new { locationIds }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    /// Parse the ticket number from an order remark. Adsolut remarks for
    /// ticket-linked orders carry "Ticket#<digits>" (optionally followed by
    /// extra text). Case-insensitive; null when no Ticket# reference is present.
    private static readonly Regex TicketRefRegex =
        new(@"Ticket#(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static long? ParseTicketNumber(string? remark)
    {
        if (string.IsNullOrWhiteSpace(remark)) return null;
        var m = TicketRefRegex.Match(remark);
        if (!m.Success) return null;
        return long.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }
}
