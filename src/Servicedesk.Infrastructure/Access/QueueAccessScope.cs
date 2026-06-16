namespace Servicedesk.Infrastructure.Access;

/// A point-in-time view of which queues a single principal may read,
/// used to mask ticket references in the activity feed and the dashboard
/// agent-activity tile. Built from <see cref="IQueueAccessService"/> and
/// passed down to the read paths so a user never sees the subject/number
/// of a ticket sitting in a queue they have no access to.
///
/// Admins always see everything (<see cref="IsAdmin"/> short-circuits the
/// set check) — mirrors how <see cref="QueueAccessService"/> treats them.
public sealed class QueueAccessScope
{
    private static readonly IReadOnlySet<Guid> Empty = new HashSet<Guid>();

    public bool IsAdmin { get; }

    /// Queue ids this principal may read. Empty for an admin (the
    /// <see cref="IsAdmin"/> flag covers them) and empty for a non-admin
    /// with no granted queues (everything is masked).
    public IReadOnlySet<Guid> QueueIds { get; }

    public QueueAccessScope(bool isAdmin, IReadOnlySet<Guid> queueIds)
    {
        IsAdmin = isAdmin;
        QueueIds = queueIds;
    }

    /// True when this principal may see the ticket in <paramref name="queueId"/>.
    public bool CanSee(Guid queueId) => IsAdmin || QueueIds.Contains(queueId);

    /// The accessible queue ids as an array, for binding to a Postgres
    /// <c>= ANY(@param)</c> clause. Empty (never null) so the clause
    /// evaluates to false rather than null for a user with no queues.
    public Guid[] ToArray() => QueueIds.Count == 0 ? Array.Empty<Guid>() : QueueIds.ToArray();

    public static QueueAccessScope AdminScope { get; } = new(true, Empty);
}

public static class QueueAccessScopeExtensions
{
    /// Resolves the caller's <see cref="QueueAccessScope"/>. Admins
    /// short-circuit without hitting the queue-access lookup; non-admins
    /// get their granted queue set (cached for 2 minutes by the service).
    public static async Task<QueueAccessScope> GetScopeAsync(
        this IQueueAccessService svc, Guid userId, string role, CancellationToken ct = default)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return QueueAccessScope.AdminScope;

        var ids = await svc.GetAccessibleQueueIdsAsync(userId, role, ct);
        return new QueueAccessScope(false, ids.ToHashSet());
    }
}
