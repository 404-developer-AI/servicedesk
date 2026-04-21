namespace Servicedesk.Infrastructure.Audit;

public sealed record AuditQuery(
    string? EventType = null,
    string? Actor = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    long? CursorId = null,
    int Limit = 50);

public sealed record AuditPage(IReadOnlyList<AuditLogEntry> Items, long? NextCursor);

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
}
