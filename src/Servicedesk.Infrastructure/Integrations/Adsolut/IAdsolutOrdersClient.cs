namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP OrderInfos endpoint (bestellingen).
/// Always pulls with IncludeFinishedState=true so finished/closed orders land
/// in the mirror too (the status filter is display-only — we mirror every
/// status). Listing uses cursor pagination; each list item already carries the
/// full order incl. detail lines, so the sync upserts straight from the page.
/// The by-id fetch exists only for a manual per-row resync and the ::-link
/// single fetch.
public interface IAdsolutOrdersClient
{
    /// One page of orders (each a full order incl. lines). Pass
    /// <paramref name="cursor"/> = null for the first page, then the previous
    /// page's NextCursor while HasNext is true. <paramref name="modifiedSince"/>
    /// scopes a delta-sync (null = full).
    Task<AdsolutOrderListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);

    /// Full order by id, including detail lines. Returns null if Adsolut
    /// answered 2xx with an empty/unparseable body.
    Task<AdsolutOrder?> GetByIdAsync(
        Guid administrationId,
        Guid orderId,
        CancellationToken ct = default);
}
