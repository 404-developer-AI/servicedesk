namespace Servicedesk.Infrastructure.Observability;

/// Persistent store of Warning/Critical events captured from Serilog sinks.
/// Each incident keeps occurring until an admin acknowledges it on
/// <c>/settings/health</c>. Dedup on (subsystem, severity, message) within a
/// 60s window bumps the existing row's <c>occurrence_count</c> instead of
/// inserting a new one, so retry storms do not flood the table.
public interface IIncidentLog
{
    /// Record a Warning/Critical event. Dedups against the most recent open
    /// row for the same (subsystem, severity, message); updates last_occurred
    /// and bumps occurrence_count if found, otherwise inserts a new row.
    Task ReportAsync(
        string subsystem,
        IncidentSeverity severity,
        string message,
        string? details,
        string? contextJson,
        CancellationToken ct);

    /// Open incidents grouped by subsystem for the Health aggregator rollup.
    Task<IReadOnlyList<IncidentRow>> ListOpenAsync(CancellationToken ct);

    /// Open (unacknowledged) incidents for the admin UI, capped.
    Task<IReadOnlyList<IncidentRow>> ListOpenRecentAsync(int take, CancellationToken ct);

    /// Archived (acknowledged) incidents for the admin UI. Optional subsystem
    /// filter; ordered by acknowledged_utc desc; capped.
    Task<IReadOnlyList<IncidentRow>> ListArchiveAsync(string? subsystem, int take, int skip, CancellationToken ct);

    /// Acknowledge a single incident. Returns the subsystem key on success
    /// (so the caller can clear subsystem-specific source state), or null if
    /// the row was already acknowledged / not found.
    Task<string?> AcknowledgeAsync(long id, Guid userId, CancellationToken ct);

    /// Bulk-acknowledge every open incident for a subsystem. Returns count.
    Task<int> AcknowledgeSubsystemAsync(string subsystem, Guid userId, CancellationToken ct);

    /// Count open incidents per subsystem, grouped by max severity. Used by
    /// HealthAggregator to bump a subsystem's rollup.
    Task<IReadOnlyDictionary<string, IncidentSeverity>> GetOpenBySubsystemAsync(CancellationToken ct);
}

public enum IncidentSeverity
{
    Warning = 1,
    Critical = 2,
}

public sealed record IncidentRow(
    long Id,
    string Subsystem,
    IncidentSeverity Severity,
    string Message,
    string? Details,
    string ContextJson,
    DateTime FirstOccurredUtc,
    DateTime LastOccurredUtc,
    int OccurrenceCount,
    DateTime? AcknowledgedUtc,
    Guid? AcknowledgedByUserId);
