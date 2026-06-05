namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged Warehouses ("magazijnen") list. Each warehouse
/// carries its locations (bins) inline, so one pass mirrors both.
public sealed record AdsolutWarehouseListPage(
    IReadOnlyList<AdsolutWarehouse> Items,
    string? NextCursor,
    bool HasNext);

/// A warehouse (stock/magazijn) reference row + its locations. Used to resolve
/// the supplier-order line's warehouse {id, code} → a human-readable name.
public sealed record AdsolutWarehouse(
    Guid Id,
    string? Code,
    string? Name,
    bool Active,
    bool Standard,
    IReadOnlyList<AdsolutWarehouseLocation> Locations);

/// A location (bin) within a warehouse. The Warehouses payload carries id +
/// name (no code), so supplier-order lines join their warehouseLocation.id to
/// this Id to resolve the display name.
public sealed record AdsolutWarehouseLocation(
    Guid Id,
    string? Name,
    bool IsDefault);
