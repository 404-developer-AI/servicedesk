using Servicedesk.Infrastructure.Integrations.Adsolut;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// Minimal IAdsolutOrderRepository test double. Only HasLinkedOrderAsync is
/// exercised (by the trigger condition pre-scan); everything else throws so a
/// test that accidentally relies on order data fails loudly rather than
/// silently. HasLinkedOrderAsync defaults to false (no linked order) and can
/// be overridden per test via the constructor.
public sealed class FakeAdsolutOrderRepository : IAdsolutOrderRepository
{
    private readonly bool _hasLinkedOrder;

    public FakeAdsolutOrderRepository(bool hasLinkedOrder = false)
        => _hasLinkedOrder = hasLinkedOrder;

    public Task<bool> HasLinkedOrderAsync(Guid ticketId, CancellationToken ct = default)
        => Task.FromResult(_hasLinkedOrder);

    public Task UpsertAsync(AdsolutOrder order, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<AdsolutOrderSyncState?> GetSyncStateAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task SaveSyncStateAsync(AdsolutOrderSyncState state, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<AdsolutOrderStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<int> GetCountAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<AdsolutOrderListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        IReadOnlyCollection<string> statusFilter, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<AdsolutOrderDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<AdsolutOrderRow>> SearchForPickerAsync(
        string? query, IReadOnlyCollection<string> statusFilter, int limit, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> LinkToTicketAsync(Guid ticketId, Guid orderId, Guid? userId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UnlinkFromTicketAsync(Guid ticketId, Guid orderId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<AdsolutOrderRow>> ListForTicketAsync(Guid ticketId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpsertSupplierOrderAsync(AdsolutSupplierOrder order, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<AdsolutSupplierOrderLineRow>> GetSupplierLinesForOrderAsync(Guid orderId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<AdsolutOrderStatusOption>> GetSupplierStatusOptionsAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpsertWarehousesAsync(IReadOnlyList<AdsolutWarehouse> warehouses, CancellationToken ct = default)
        => throw new NotImplementedException();
}
