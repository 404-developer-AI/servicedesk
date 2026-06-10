namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP Contracts endpoint (contracten). The
/// list returns every contract regardless of status (the endpoint has no state
/// filter — narrowing is entirely our side, and the status filter is
/// display-only). Listing uses cursor pagination; each list item already
/// carries the full contract incl. its article lines, so the sync upserts
/// straight from the page. The by-id fetch exists only for a manual per-row
/// resync.
public interface IAdsolutContractsClient
{
    /// One page of contracts (each a full contract incl. article lines). Pass
    /// <paramref name="cursor"/> = null for the first page, then the previous
    /// page's NextCursor while HasNext is true. <paramref name="modifiedSince"/>
    /// scopes a delta-sync (null = full).
    Task<AdsolutContractListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);

    /// Full contract by id, including article lines. Returns null if Adsolut
    /// answered 2xx with an empty/unparseable body.
    Task<AdsolutContract?> GetByIdAsync(
        Guid administrationId,
        Guid contractId,
        CancellationToken ct = default);
}
