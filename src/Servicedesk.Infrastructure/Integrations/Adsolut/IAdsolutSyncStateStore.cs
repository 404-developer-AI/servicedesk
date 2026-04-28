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
}

public interface IAdsolutSyncStateStore
{
    Task<AdsolutSyncState?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AdsolutSyncState state, CancellationToken ct = default);
}
