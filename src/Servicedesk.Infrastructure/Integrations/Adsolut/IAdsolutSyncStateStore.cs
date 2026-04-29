namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Singleton row in <c>adsolut_sync_state</c>. Carries the cursor + the
/// latest tick's totals so the admin "Sync" panel can render without
/// joining audit rows. Counters are cumulative-since-last-tick (reset
/// each tick) — long-term aggregates live in <c>integration_audit</c>.
public sealed class AdsolutSyncState
{
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastDeltaSyncUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int CompaniesSeen { get; set; }
    public int CompaniesUpserted { get; set; }
    public int CompaniesSkippedLoserInConflict { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// Set by the admin "Acknowledge" action on the integrations health
    /// tile. Sync-health is considered cleared as long as
    /// <c>AcknowledgedUtc &gt;= LastErrorUtc</c>; the next failed tick
    /// pushes LastErrorUtc forward and undoes the acknowledgement.
    public DateTime? AcknowledgedUtc { get; set; }
}

public interface IAdsolutSyncStateStore
{
    Task<AdsolutSyncState?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AdsolutSyncState state, CancellationToken ct = default);
    /// Stamps <c>acknowledged_utc = now()</c> on the singleton row, leaving
    /// every other column untouched. Idempotent — repeated calls just
    /// advance the timestamp.
    Task AcknowledgeAsync(CancellationToken ct = default);
}
