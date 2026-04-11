namespace Servicedesk.Infrastructure.Audit;

/// Row read back from <c>audit_log</c>. Separate from <see cref="AuditEvent"/>
/// because rows carry server-assigned fields (id, utc, hash chain).
public sealed record AuditLogEntry(
    long Id,
    DateTime Utc,
    string Actor,
    string ActorRole,
    string EventType,
    string? Target,
    string? ClientIp,
    string? UserAgent,
    string PayloadJson,
    byte[] PrevHash,
    byte[] EntryHash);
