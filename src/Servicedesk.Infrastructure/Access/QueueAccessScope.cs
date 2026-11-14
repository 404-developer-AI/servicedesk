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

    /// The scope a search principal carries: admins see every queue, any
    /// other role exactly its granted queues (none when the list is null).
    public static QueueAccessScope For(Servicedesk.Domain.Search.SearchPrincipal principal) =>
        principal.IsAdmin
            ? AdminScope
            : new QueueAccessScope(false, (principal.AllowedQueueIds ?? Array.Empty<Guid>()).ToHashSet());

    /// Repository convention: a null id list means "admin, no filter".
    public static QueueAccessScope FromNullable(IReadOnlyCollection<Guid>? accessibleQueueIds) =>
        accessibleQueueIds is null ? AdminScope : new QueueAccessScope(false, accessibleQueueIds.ToHashSet());

    // ---- SQL ---------------------------------------------------------------
    //
    // The one way a query restricts rows to the caller's queues. Every read
    // path that filters tickets (or rows hanging off a ticket) by queue
    // access writes `scope.SqlPredicate("t.queue_id")` into its WHERE clause
    // and binds `scope.ToArray()` as `@AccessibleQueueIds` — so the rule, the
    // parameter name and the admin short-cut are identical everywhere.

    /// Names of the bound parameters (see <see cref="AddTo"/>).
    public const string QueueIdsParameter = "AccessibleQueueIds";
    public const string IsAdminParameter = "AccessIsAdmin";

    /// Constant form of the rule for `t.queue_id`, for SQL that must stay a
    /// compile-time constant (e.g. per-row masking inside CASE). Prefer
    /// <see cref="SqlPredicate"/> in WHERE clauses — it drops the OR for
    /// admins and keeps the agent variant index-friendly.
    public const string TicketQueueVisibleSql = "(@AccessIsAdmin OR t.queue_id = ANY(@AccessibleQueueIds))";

    private static readonly System.Text.RegularExpressions.Regex SafeSqlExpression =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// SQL predicate limiting <paramref name="queueIdExpression"/> (a column
    /// such as <c>t.queue_id</c>, never user input) to this scope. Admins get
    /// <c>TRUE</c> — no OR in the plan, so the queue index stays usable for
    /// the agent variant. A non-admin without queues matches nothing.
    public string SqlPredicate(string queueIdExpression)
    {
        if (!SafeSqlExpression.IsMatch(queueIdExpression))
            throw new ArgumentException("Queue id expression must be a plain column reference (column or alias.column).", nameof(queueIdExpression));
        return IsAdmin ? "TRUE" : $"{queueIdExpression} = ANY(@{QueueIdsParameter})";
    }

    /// Binds both parameters on a Dapper parameter bag.
    public void AddTo(Dapper.DynamicParameters parameters)
    {
        parameters.Add(QueueIdsParameter, ToArray());
        parameters.Add(IsAdminParameter, IsAdmin);
    }

    /// A parameter bag holding <paramref name="values"/> (an anonymous
    /// object, may be null) plus this scope's parameters.
    public Dapper.DynamicParameters Parameters(object? values = null)
    {
        var p = new Dapper.DynamicParameters(values);
        AddTo(p);
        return p;
    }
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
