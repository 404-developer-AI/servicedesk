using Servicedesk.Domain.Views;

namespace Servicedesk.Infrastructure.Access;

public interface IViewAccessService
{
    Task<IReadOnlyList<View>> GetAccessibleViewsAsync(Guid userId, string role, CancellationToken ct = default);
    Task<bool> HasViewAccessAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetDirectViewIdsAsync(Guid userId, CancellationToken ct = default);
    Task SetDirectViewAccessAsync(Guid userId, IReadOnlyList<Guid> viewIds, CancellationToken ct = default);
    void InvalidateCache(Guid userId);
    void InvalidateAllViewCaches();
}
