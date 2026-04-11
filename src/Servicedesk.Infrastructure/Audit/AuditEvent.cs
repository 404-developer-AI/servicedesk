namespace Servicedesk.Infrastructure.Audit;

/// Input to <see cref="IAuditLogger.LogAsync"/>. Immutable, explicit fields so
/// no implicit ambient context leaks into the audit trail.
public sealed record AuditEvent(
    string EventType,
    string Actor,
    string ActorRole,
    string? Target = null,
    string? ClientIp = null,
    string? UserAgent = null,
    object? Payload = null);
