namespace Servicedesk.Infrastructure.Access;

public interface IQueueAccessService
{
    Task<IReadOnlyList<Guid>> GetAccessibleQueueIdsAsync(Guid userId, string role, CancellationToken ct = default);
    Task<bool> HasQueueAccessAsync(Guid userId, string role, Guid queueId, CancellationToken ct = default);
    Task SetQueueAccessAsync(Guid userId, IReadOnlyList<Guid> queueIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetUsersForQueueAsync(Guid queueId, CancellationToken ct = default);
    void InvalidateCache(Guid userId);
}
