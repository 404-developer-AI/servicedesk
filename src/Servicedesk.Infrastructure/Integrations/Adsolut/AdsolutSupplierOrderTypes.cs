namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged SupplierOrderInfos ("bestellingen", Doc "BL")
/// list. Like OrderInfos, the list returns the full supplier order incl. its
/// lines inline, so the sync upserts straight from the page.
public sealed record AdsolutSupplierOrderListPage(
    IReadOnlyList<AdsolutSupplierOrder> Items,
    string? NextCursor,
    bool HasNext);

/// A supplier order (inkooporder) header + its detail lines, as mirrored from
/// SupplierOrderInfos. The supplier (leverancier) + order date live on the
/// header and get denormalised onto each line row at upsert time.
public sealed record AdsolutSupplierOrder(
    Guid Id,
    int? DocNr,
    string? BookCode,
    Guid? SupplierId,
    string? SupplierCode,
    string? SupplierName,
    string? StateCode,
    DateTimeOffset? SupplierOrderDate,
    DateTimeOffset? AdsolutLastModified,
    IReadOnlyList<AdsolutSupplierOrderLine> Lines);

public sealed record AdsolutSupplierOrderLine(
    Guid Id,
    int? LineNr,
    Guid? ProductId,
    string? ProductCode,
    string? Name,
    string? Description,
    decimal? Quantity,
    decimal? Delivered,
    string? UnitCode,
    decimal? GrossUnitPrice,
    decimal? UnitPrice,
    decimal? Discount1,
    /// supplierOrderDetailState.code — the real per-line procurement status
    /// ("ONTV" = Ontvangen, "OPEN", …). This is the value the order-detail
    /// "Bestelling status" shows.
    string? StatusCode,
    /// orderInfoDetail.orderInfoId — links back to the sales order HEADER
    /// (adsolut_orders.id). There is no order-LINE id on the supplier line.
    Guid? LinkedOrderId,
    int? LinkedOrderDocNr);
