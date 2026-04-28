namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Result of a single upsert attempt against the local <c>companies</c>
/// table. The sync worker rolls these up per tick into the counters on
/// <c>adsolut_sync_state</c>.
public enum AdsolutUpsertOutcome
{
    /// Adsolut row was UPDATEd onto an existing matched company.
    Updated,
    /// Adsolut row triggered an INSERT (no match, create-toggle ON).
    Created,
    /// Local company row was newer than the Adsolut row (last-write-wins
    /// tie-breaker decided in favour of the local edit).
    SkippedLocalNewer,
    /// A match exists but the update-toggle is OFF.
    SkippedUpdateToggleOff,
    /// No match exists and the create-toggle is OFF.
    SkippedCreateToggleOff,
}

/// One transaction's worth of toggles + cursor — captured at the start of
/// a tick so a settings-edit mid-tick can't change behaviour for the rows
/// already partially processed. <c>FreemailBlacklist</c> is loaded once per
/// tick from <see cref="Servicedesk.Infrastructure.Settings.SettingKeys.Mail.AutoLinkDomainBlacklist"/>
/// (same source the mail-ingest path uses) so the two intake-paths can
/// never disagree on which domains count as freemail.
public sealed record AdsolutSyncOptions(
    bool PullUpdateEnabled,
    bool PullCreateEnabled,
    bool LinkCompanyDomainsFromEmail = false,
    IReadOnlySet<string>? FreemailBlacklist = null);

/// Idempotent upsert of one Adsolut customer (or supplier) into the
/// servicedesk <c>companies</c> table. Match precedence:
/// <list type="number">
/// <item>By <c>companies.adsolut_id</c> — already linked.</item>
/// <item>By <c>companies.code</c> — first-link case (Adsolut code matches
/// an existing local company that arrived through some other channel).
/// On a successful match by code, <c>adsolut_id</c> is filled in so the
/// next tick takes the fast path.</item>
/// <item>No match → INSERT (when create-toggle is ON).</item>
/// </list>
/// Conflict tie-breaker: latest timestamp wins (<c>companies.updated_utc</c>
/// vs Adsolut <c>lastModified</c>). The worker captures this snapshot
/// outside the transaction so a fresh local edit during the upsert is
/// not lost in a race — the tie-breaker will simply pick it next tick.
public interface IAdsolutCompanyUpserter
{
    Task<AdsolutUpsertOutcome> UpsertAsync(
        AdsolutCustomer customer,
        AdsolutSyncOptions options,
        CancellationToken ct = default);
}
