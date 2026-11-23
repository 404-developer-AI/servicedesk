using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// <see cref="IQueueAccessService"/> with a fixed answer for every user and
/// role: access to exactly the given queues, or to every queue
/// (<see cref="All"/>) / none (<see cref="None"/>).
public sealed class FakeQueueAccess : IQueueAccessService
{
    private readonly Guid[] _queueIds;
    private readonly bool _allowAll;

    public FakeQueueAccess(params Guid[] queueIds) => _queueIds = queueIds;

    private FakeQueueAccess(bool allowAll)
    {
        _queueIds = Array.Empty<Guid>();
        _allowAll = allowAll;
    }

    public static FakeQueueAccess All() => new(allowAll: true);
    public static FakeQueueAccess None() => new(allowAll: false);
    public static FakeQueueAccess Allow(bool allow) => new(allowAll: allow);

    public Task<IReadOnlyList<Guid>> GetAccessibleQueueIdsAsync(Guid userId, string role, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(_queueIds);

    public Task<bool> HasQueueAccessAsync(Guid userId, string role, Guid queueId, CancellationToken ct = default)
        => Task.FromResult(_allowAll || _queueIds.Contains(queueId));

    public Task SetQueueAccessAsync(Guid userId, IReadOnlyList<Guid> queueIds, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> GetUsersForQueueAsync(Guid queueId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    public void InvalidateCache(Guid userId) { }
}
