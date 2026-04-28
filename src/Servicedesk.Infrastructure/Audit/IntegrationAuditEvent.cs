namespace Servicedesk.Infrastructure.Audit;

/// Outcome of an integration call. Constrained to a small whitelist that
/// the DB CHECK constraint mirrors so the admin overview can colour-code
/// rows without a string-soup of variants. <c>warn</c> is for transient
/// failures expected to recover (network blip, 5xx); <c>error</c> is for
/// terminal conditions where the admin must intervene (invalid_grant,
/// missing config).
public enum IntegrationAuditOutcome
{
    Ok,
    Warn,
    Error,
}

/// Input record for <see cref="IIntegrationAuditLogger.LogAsync"/>. All
/// non-essential fields are nullable so a healthcheck-tick row can omit
/// http_status (no upstream call happened) and a code-exchange row can
/// omit actor (system-callback context).
public sealed record IntegrationAuditEvent(
    string Integration,
    string EventType,
    IntegrationAuditOutcome Outcome,
    string? Endpoint = null,
    int? HttpStatus = null,
    int? LatencyMs = null,
    string? ActorId = null,
    string? ActorRole = null,
    string? ErrorCode = null,
    object? Payload = null);

/// One row from <c>integration_audit</c>. Mapped via Dapper with
/// <c>AS PascalCase</c> aliases per project convention.
public sealed class IntegrationAuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset Utc { get; set; }
    public string Integration { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Endpoint { get; set; }
    public int? HttpStatus { get; set; }
    public int? LatencyMs { get; set; }
    public string? ActorId { get; set; }
    public string? ActorRole { get; set; }
    public string? ErrorCode { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed record IntegrationAuditPage(
    IReadOnlyList<IntegrationAuditEntry> Items,
    long? NextCursor);
