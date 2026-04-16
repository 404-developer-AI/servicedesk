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
    public Guid UserId { get; }
    public string Role { get; }
    public IReadOnlyList<Guid>? AllowedQueueIds { get; }

    public SearchPrincipal(Guid userId, string role, IReadOnlyList<Guid>? allowedQueueIds)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Role is required.", nameof(role));

        UserId = userId;
        Role = role;
        AllowedQueueIds = allowedQueueIds;
    }

    public bool IsAdmin => string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase);
    public bool IsAgent => string.Equals(Role, "Agent", StringComparison.OrdinalIgnoreCase);
    public bool IsCustomer => string.Equals(Role, "Customer", StringComparison.OrdinalIgnoreCase);
}
