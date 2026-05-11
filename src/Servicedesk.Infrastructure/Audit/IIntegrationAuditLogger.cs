namespace Servicedesk.Infrastructure.Audit;

/// Operational log for outbound integration calls (Adsolut today; Zammad,
/// TRMM later). Distinct from <see cref="IAuditLogger"/>: this surface is
/// not hash-chained and does not require an actor — scheduler ticks have
/// neither — but it does carry the latency, http_status and upstream
/// error_code that an admin needs to spot a slow or failing integration.
/// audit_log keeps the security trail; integration_audit answers "is this
/// integration healthy and how slow has it been".
public interface IIntegrationAuditLogger
{
    Task LogAsync(IntegrationAuditEvent evt, CancellationToken cancellationToken = default);

    /// Deletes rows whose <c>utc</c> is older than <paramref name="cutoffUtc"/>
    /// and whose <c>event_type</c> matches one of <paramref name="eventTypes"/>.
    /// Returns the row-count actually deleted so a caller can surface the
    /// sweep result. Scoped intentionally narrow: admin-action and rare-PAPI
    /// rows must NEVER be auto-deleted; only the firehose event-types
    /// (poll-summary, healthcheck heartbeat) are passed in by the caller.
    Task<int> DeleteOldByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}
