using Servicedesk.Domain.ViewGroups;

namespace Servicedesk.Infrastructure.Persistence.ViewGroups;

public interface IViewGroupRepository
{
    Task<IReadOnlyList<ViewGroupSummary>> ListAsync(CancellationToken ct);
    Task<ViewGroupDetail?> GetDetailAsync(Guid groupId, CancellationToken ct);
    Task<ViewGroup> CreateAsync(string name, string description, int sortOrder, CancellationToken ct);
    Task<ViewGroup?> UpdateAsync(Guid id, string name, string description, int sortOrder, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task SetMembersAsync(Guid groupId, IReadOnlyList<Guid> userIds, CancellationToken ct);
    Task SetViewsAsync(Guid groupId, IReadOnlyList<Guid> viewIds, CancellationToken ct);
}
