using Servicedesk.Domain.Views;

namespace Servicedesk.Infrastructure.Persistence.Views;

public interface IViewRepository
{
    Task<IReadOnlyList<View>> ListAsync(Guid userId, CancellationToken ct);
    Task<View?> GetAsync(Guid id, CancellationToken ct);
    Task<View> CreateAsync(Guid userId, string name, string filtersJson, string? columns, int sortOrder, bool isShared, string displayConfigJson, CancellationToken ct);
    Task<View?> UpdateAsync(Guid id, string name, string filtersJson, string? columns, int sortOrder, bool isShared, string displayConfigJson, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
