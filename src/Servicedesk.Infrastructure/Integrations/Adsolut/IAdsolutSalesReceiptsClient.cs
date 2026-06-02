namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP SalesReceiptInfos endpoint
/// (verkoopbonnen). Always pulls with IncludeFinishedState=true — invoiced /
/// finished receipts (the bulk) are excluded by default and we want them all
/// in the mirror. Listing uses cursor pagination; the per-receipt detail is
/// fetched by-id because the list view omits performance lines.
public interface IAdsolutSalesReceiptsClient
{
    /// One page of receipts. Pass <paramref name="cursor"/> = null for the
    /// first page, then the previous page's NextCursor while HasNext is true.
    /// <paramref name="modifiedSince"/> scopes a delta-sync (null = full).
    Task<AdsolutSalesReceiptListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);

    /// Full receipt by id, including product + performance lines. Returns null
    /// if Adsolut answered 2xx with an empty/unparseable body.
    Task<AdsolutSalesReceipt?> GetByIdAsync(
        Guid administrationId,
        Guid receiptId,
        CancellationToken ct = default);
}
