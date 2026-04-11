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
}
