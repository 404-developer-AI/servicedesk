namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged CatalogueProducts list. The list view returns
/// the full catalogue record inline (code, multi-language name, the
/// serviceProduct / isActive / blocked / endOfSeries flags), so each item is a
/// complete <see cref="AdsolutCatalogueProduct"/> ready to upsert — the sync
/// never needs a per-product by-id fetch. <see cref="NextCursor"/> +
/// <see cref="HasNext"/> drive cursor pagination (pagingData).
public sealed record AdsolutCatalogueProductListPage(
    IReadOnlyList<AdsolutCatalogueProduct> Items,
    string? NextCursor,
    bool HasNext);

/// One Adsolut ERP catalogue product (the products that can appear on a
/// verkoopbon) as mirrored from the CatalogueProducts endpoint. This is a
/// separate catalogue from the contract Articles mirror: it feeds the
/// Timesheet → Adsolut "VK Werkuren" matching, where an admin flags which of
/// these products count as billable work hours. <see cref="Name"/> is the Nl
/// value picked from the API's multi-language Translation[] array.
public sealed record AdsolutCatalogueProduct(
    Guid Id,
    string? Code,
    string? Name,
    bool ServiceProduct,
    bool IsActive,
    bool Blocked,
    bool EndOfSeries,
    DateTimeOffset? AdsolutCreatedUtc,
    DateTimeOffset? AdsolutLastModified);
