namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged Contracts list. Like OrderInfos (and unlike
/// SalesReceiptInfos), the Contracts list view returns the FULL contract
/// including its article lines inline, so each item is a complete
/// <see cref="AdsolutContract"/> ready to upsert — the sync never needs a
/// per-contract by-id fetch. <see cref="NextCursor"/> + <see cref="HasNext"/>
/// drive cursor pagination (pagingData).
public sealed record AdsolutContractListPage(
    IReadOnlyList<AdsolutContract> Items,
    string? NextCursor,
    bool HasNext);

/// Full Adsolut contract (header + article lines) as mirrored from the ERP
/// Contracts endpoint. A contract carries NO Ticket# reference — it keys off
/// the customer (<see cref="CustomerAdsolutId"/> maps 1:1 to
/// companies.adsolut_id). <see cref="StateCode"/>/<see cref="StateDescription"/>
/// come from the inline contractState (code + per-language label), so no
/// separate ContractStates lookup is needed. Totals are stored verbatim.
public sealed record AdsolutContract(
    Guid Id,
    int? DocNr,
    Guid? CustomerAdsolutId,
    Guid? InvoiceCustomerAdsolutId,
    string? CustomerName,
    string? StateCode,
    string? StateDescription,
    DateTimeOffset? DocDate,
    DateTimeOffset? StartDate,
    DateTimeOffset? StopDate,
    DateTimeOffset? EndDate,
    string? Description,
    string? Memo,
    string? PeriodicityCode,
    string? PeriodicityLabel,
    string? InvoicingPeriodicityCode,
    string? InvoicingPeriodicityLabel,
    int? NumberOfTerms,
    decimal TotalExclVat,
    decimal TotalVat,
    decimal TotalInclVat,
    DateTimeOffset? AdsolutCreatedUtc,
    DateTimeOffset? AdsolutLastModified,
    IReadOnlyList<AdsolutContractLine> Lines);

/// One contract article line (contractDetailArticles). The payload has no line
/// number, so <see cref="LineNr"/> is the array ordinal assigned at parse time
/// for a stable display order. Article name + description are inline, so the
/// articleId need not be resolved for display.
public sealed record AdsolutContractLine(
    Guid Id,
    int? LineNr,
    Guid? ArticleId,
    string? Name,
    string? Description,
    decimal? Quantity,
    decimal? GrossUnitPrice,
    decimal? Discount1,
    decimal? Discount2,
    decimal? UnitPrice,
    decimal? UnitPriceIncl,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate);
