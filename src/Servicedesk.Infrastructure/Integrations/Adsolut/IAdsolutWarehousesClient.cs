namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP Warehouses endpoint
/// (GET /erp/v1/adm/{adm}/Warehouses). Small reference list — cursor-paged,
/// each item carries its locations inline. Mirrored so supplier-order lines can
/// resolve their warehouse/location code+id to a display name at read time.
public interface IAdsolutWarehousesClient
{
    Task<AdsolutWarehouseListPage> ListPageAsync(
        Guid administrationId,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);
}
