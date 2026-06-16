namespace Servicedesk.Domain.Search;

/// Immutable authorization context for every search call. The search façade
/// refuses to execute without one, so row-level scoping is compile-time
/// enforced rather than something an endpoint could forget to apply.
///
/// <see cref="AllowedQueueIds"/> semantics: <c>null</c> means "no queue
/// restriction" (Admin); an empty list means "this user has zero queues"
/// and must yield zero ticket/mail results; a non-empty list scopes ticket
/// and mail results to those queues.
public sealed record SearchPrincipal
{
    private static readonly IReadOnlySet<string> NoFeatures = new HashSet<string>();

    public Guid UserId { get; }
    public string Role { get; }
    public IReadOnlyList<Guid>? AllowedQueueIds { get; }

    /// Per-user feature flags (see <see cref="SearchFeature"/>) that gate
    /// availability of feature-flagged sources (Orders, Contracts family,
    /// Employee Feedback, …). Resolved once when the principal is built so
    /// <see cref="ISearchSource.IsAvailableFor"/> can hide those sources from
    /// the search dropdown without each source doing its own DB round-trip.
    /// Empty for a principal built without flags (e.g. unit tests) and for
    /// users with none of them set. Admins ignore this — their availability
    /// short-circuits on role.
    public IReadOnlySet<string> Features { get; }

    public SearchPrincipal(
        Guid userId, string role, IReadOnlyList<Guid>? allowedQueueIds,
        IReadOnlySet<string>? features = null)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Role is required.", nameof(role));

        UserId = userId;
        Role = role;
        AllowedQueueIds = allowedQueueIds;
        Features = features ?? NoFeatures;
    }

    public bool IsAdmin => string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase);
    public bool IsAgent => string.Equals(Role, "Agent", StringComparison.OrdinalIgnoreCase);
    public bool IsCustomer => string.Equals(Role, "Customer", StringComparison.OrdinalIgnoreCase);

    /// True when this principal has the given per-user feature flag enabled.
    public bool HasFeature(string feature) => Features.Contains(feature);
}
