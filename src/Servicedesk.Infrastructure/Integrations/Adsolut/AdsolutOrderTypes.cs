namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged OrderInfos list. Unlike SalesReceipts, the
/// Orders list view returns the FULL order including its detail lines inline,
/// so each item is a complete <see cref="AdsolutOrder"/> ready to upsert — the
/// sync never needs a per-order by-id fetch. <see cref="NextCursor"/> +
/// <see cref="HasNext"/> drive cursor pagination (pagingData).
public sealed record AdsolutOrderListPage(
    IReadOnlyList<AdsolutOrder> Items,
    string? NextCursor,
    bool HasNext);

/// Full Adsolut order (header + detail lines) as mirrored from OrderInfos.
/// The API exposes header totals directly (totalPriceExclVat /
/// totalPriceInclVat), so no client-side compute is needed.
public sealed record AdsolutOrder(
    Guid Id,
    int? DocNr,
    string? BookCode,
    string? KluwerRef,
    Guid? CustomerAdsolutId,
    string? CustomerCode,
    string? CustomerName,
    Guid? StateId,
    string? StateCode,
    string? StateDescription,
    DateTimeOffset? OrderDate,
    DateTimeOffset? RequestedDeliveryDate,
    DateTimeOffset? ConfirmedDeliveryDate,
    string? Remark,
    string? InternalMemo,
    string? RepresentativeCode,
    string? RepresentativeName,
    string? CurrencyIso,
    decimal TotalExclVat,
    decimal TotalInclVat,
    DateTimeOffset? AdsolutCreatedUtc,
    DateTimeOffset? AdsolutLastModified,
    IReadOnlyList<AdsolutOrderLine> Lines);

public sealed record AdsolutOrderLine(
    Guid Id,
    int? LineNr,
    Guid? ProductId,
    string? ProductCode,
    string? Description,
    decimal? Quantity,
    string? UnitCode,
    string? UnitDescription,
    decimal? Delivered,
    decimal? GrossUnitPrice,
    decimal? UnitPrice,
    decimal? Discount1,
    decimal? Discount2,
    decimal? PriceExclVat,
    decimal? PriceInclVat,
    string? VatCode,
    string? VatDescription,
    string? StateCode,
    string? StateDescription,
    DateTimeOffset? RequestedDeliveryDate,
    DateTimeOffset? ConfirmedDeliveryDate);
