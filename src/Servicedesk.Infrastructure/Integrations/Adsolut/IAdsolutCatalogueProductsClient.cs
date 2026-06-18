namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP CatalogueProducts endpoint — the full
/// product catalogue that can be used on verkoopbonnen. Listing uses cursor
/// pagination; each list item already carries the full record (code,
/// multi-language name, the serviceProduct / isActive / blocked / endOfSeries
/// flags), so the sync upserts straight from the page (no per-product by-id
/// fetch).
public interface IAdsolutCatalogueProductsClient
{
    /// One page of catalogue products. Pass <paramref name="cursor"/> = null for
    /// the first page, then the previous page's NextCursor while HasNext is true.
    /// <paramref name="modifiedSince"/> scopes a delta-sync (null = full).
    Task<AdsolutCatalogueProductListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);
}
