namespace Servicedesk.Infrastructure.Audit;

public sealed record AuditQuery(
    string? EventType = null,
    string? Actor = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    long? CursorId = null,
    int Limit = 50);

public sealed record AuditPage(IReadOnlyList<AuditLogEntry> Items, long? NextCursor);

/// One row of a top-N breakdown: a normalised label and its count.
public sealed record AuditTopItem(string Label, int Count);

/// v0.1.4 — "what and who" behind a burst of audit events: the most
/// frequent targets (request paths with ids normalised, prefixed by the
/// HTTP method when the payload carries one) and the most frequent client
/// IPs, plus the number of distinct IPs. Lets the security-activity alert
/// say <em>which</em> endpoint and <em>how many</em> sources are behind a
/// threshold breach instead of a bare count.
public sealed record AuditTopBreakdown(
    IReadOnlyList<AuditTopItem> TopTargets,
    IReadOnlyList<AuditTopItem> TopSources,
    int DistinctSources)
{
    public static readonly AuditTopBreakdown Empty = new(Array.Empty<AuditTopItem>(), Array.Empty<AuditTopItem>(), 0);
}

public interface IAuditQuery
{
    Task<AuditPage> ListAsync(AuditQuery query, CancellationToken cancellationToken = default);
    Task<AuditLogEntry?> GetAsync(long id, CancellationToken cancellationToken = default);
    /// Contact-scoped history — unions events targeted at the contact
    /// (<c>target = contactId</c>) with events whose payload references
    /// the contact via <c>payload-&gt;&gt;'contactId'</c> (e.g.
    /// <c>company.contact.linked</c> / <c>company.contact.unlinked</c>).
    Task<AuditPage> ListForContactAsync(Guid contactId, long? cursorId, int limit, CancellationToken cancellationToken = default);
    /// Aggregate count per event-type within a half-open time window
    /// (<c>fromUtc &lt;= utc &lt; toUtc</c>). Returns only the event-types
    /// passed in; types with zero matches are absent from the result. Used
    /// by the security-activity monitor to evaluate thresholds without
    /// pulling full rows back to user-space.
    Task<IReadOnlyDictionary<string, int>> CountByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    /// v0.1.4 — top-N targets and client IPs behind the given event-types in
    /// a half-open window. Targets are normalised (GUIDs and numeric path
    /// segments collapsed) so one endpoint shows as one row. Only called for
    /// categories that already breached their threshold, so cost is bounded.
    Task<AuditTopBreakdown> TopByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int top,
        CancellationToken cancellationToken = default);
}
