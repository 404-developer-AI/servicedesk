namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP SupplierOrderInfos endpoint
/// (bestellingen / inkooporders, Doc "BL"). Always pulls with
/// IncludeReceivedState=true (the endpoint's real status filter — its default
/// drops received/"ontv" orders); cursor pagination; each list item carries the
/// full supplier order incl. lines, so the sync upserts straight from the page.
public interface IAdsolutSupplierOrdersClient
{
    Task<AdsolutSupplierOrderListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);
}
