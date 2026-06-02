namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged SalesReceiptInfos list. The list view is
/// "light" — it omits the performance lines — so we only lift the fields the
/// sync needs to decide what to fetch by-id: the receipt id, its state code
/// (for the status filter, applied before the by-id fetch) and lastModified
/// (delta bookkeeping). The full receipt comes from <see cref="AdsolutSalesReceipt"/>.
public sealed record AdsolutSalesReceiptListPage(
    IReadOnlyList<AdsolutSalesReceiptListItem> Items,
    string? NextCursor,
    bool HasNext);

public sealed record AdsolutSalesReceiptListItem(
    Guid Id,
    string? StateCode,
    DateTimeOffset? LastModified);

/// Full SalesReceipt as mirrored from the by-id endpoint. There is no header
/// total in the API; the caller computes total_excl_vat from the line-sets.
public sealed record AdsolutSalesReceipt(
    Guid Id,
    int? DocNr,
    string? BookCode,
    Guid? CustomerAdsolutId,
    string? CustomerCode,
    string? CustomerName,
    Guid? StateId,
    string? StateCode,
    string? StateDescription,
    DateTimeOffset? SalesReceiptDate,
    string? Description,
    string? InternalMemo,
    string? Memo,
    string? EmployeeCode,
    string? EmployeeName,
    string? EmployeeEmail,
    string? RepresentativeCode,
    string? RepresentativeName,
    string? CurrencyIso,
    bool VatIncluded,
    DateTimeOffset? AdsolutCreatedUtc,
    DateTimeOffset? AdsolutLastModified,
    IReadOnlyList<AdsolutSalesReceiptLine> Lines,
    IReadOnlyList<AdsolutSalesReceiptPerformance> Performances);

public sealed record AdsolutSalesReceiptLine(
    Guid Id,
    int? LineNr,
    string? ProductCode,
    string? Name,
    string? Description,
    decimal? Quantity,
    string? UnitCode,
    decimal? UnitPrice,
    decimal? TotalExclVat,
    decimal? TotalInclVat,
    string? VatCode);

public sealed record AdsolutSalesReceiptPerformance(
    Guid Id,
    string? EmployeeCode,
    DateTimeOffset? PerformanceDate,
    string? FromTime,
    string? UntilTime,
    decimal? DurationMinutes,
    decimal? InvoiceDurationMinutes,
    decimal? InvoiceUnitPrice,
    decimal? InvoiceTotal,
    string? PerformanceCode,
    string? Description);
