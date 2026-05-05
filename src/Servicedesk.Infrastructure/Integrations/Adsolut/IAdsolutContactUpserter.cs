namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Result of one contact-upsert attempt. The sync worker rolls these up
/// per tick into the counters on <c>adsolut_sync_state</c> (alongside the
/// existing companies-tak counters).
public enum AdsolutContactUpsertOutcome
{
    /// Existing <c>contact_companies</c> link UPDATEd in place (and possibly
    /// the contact-level fields too when this inbound row was the freshest
    /// known Adsolut state for the person).
    Updated,

    /// New row inserted into <c>contact_companies</c>. May also have
    /// inserted a new <c>contacts</c> row when the email did not yet exist
    /// SD-side. Both inserts happen inside the same transaction.
    Created,

    /// v0.0.28 inbound no-op guard: the inbound row's hash equals the
    /// last-stored hash on the link AND <c>adsolut_active</c> matches.
    /// Closes the echo-pull loop on the per-link state and avoids
    /// redundant SignalR/audit noise.
    SkippedNoChange,

    /// SD's <c>contacts.updated_utc</c> is more recent than the inbound
    /// <c>lastModified</c>. Contact-level fields are protected; the link
    /// state IS still synced, but the outcome reports the SD-wins
    /// tie-breaker so the operational log stays honest.
    SkippedLocalNewer,

    /// A match exists but the update-toggle is OFF.
    SkippedUpdateToggleOff,

    /// No match exists and the create-toggle is OFF.
    SkippedCreateToggleOff,

    /// Inbound contact has no email. SD's email-keyed contacts schema
    /// can't host this row; logged once to <c>integration_audit</c> by
    /// the worker so an admin can spot-check the offender in Adsolut.
    SkippedNoEmail,

    /// The same Adsolut UUID is already linked to a different SD company.
    /// Should not happen for legitimate Adsolut data — each row is bound
    /// to exactly one customer upstream. Recoverable: the next reconcile
    /// pass corrects the state once Adsolut clears the conflict.
    SkippedLinkCompanyMismatch,
}

/// One transaction's worth of contact-pull toggles. Captured at the start
/// of a tick so a settings-edit mid-tick can't change behaviour for the
/// rows already partially processed (mirrors <see cref="AdsolutSyncOptions"/>).
public sealed record AdsolutContactsSyncOptions(
    bool PullUpdateEnabled,
    bool PullCreateEnabled);

/// Idempotent upsert of one Adsolut customer-contact (or supplier-contact)
/// into the SD <c>contacts</c> + <c>contact_companies</c> tables. Match
/// precedence:
/// <list type="number">
/// <item>By <c>contact_companies.adsolut_contact_id</c> — the link is
/// already linked. Confirms the company matches; refuses cross-company
/// rebind to keep the data shape sane.</item>
/// <item>By <c>contacts.email</c> (CITEXT, case-insensitive) — the
/// person already exists SD-side. We then look for a same-(contact,
/// company) link to upgrade with the Adsolut UUID; otherwise insert a
/// fresh link with role 'primary' if first link, else 'secondary'.</item>
/// <item>No match → INSERT both <c>contacts</c> + <c>contact_companies</c>
/// when the create-toggle is ON.</item>
/// </list>
/// Conflict tie-breakers:
/// <list type="bullet">
/// <item><b>Per-link state</b> (UUID, lastModified, active, hash) is
/// always synced from Adsolut — there is no SD-side equivalent to
/// conflict with.</item>
/// <item><b>Contact-level fields</b> (first_name, last_name, phone,
/// mobile_phone) are LWW: written only when this inbound row is the
/// freshest known Adsolut state for the person (lastModified ≥ max of
/// other links' adsolut_last_modified) AND
/// <c>contacts.updated_utc</c> ≤ inbound.lastModified.</item>
/// </list>
public interface IAdsolutContactUpserter
{
    Task<AdsolutContactUpsertOutcome> UpsertAsync(
        Guid companyId,
        AdsolutContact contact,
        AdsolutContactsSyncOptions options,
        CancellationToken ct = default);

    /// Reconcile-loop helper: given the fresh full set of Adsolut UUIDs
    /// returned for one company, flip every link whose UUID is no longer
    /// in that set to <c>adsolut_active=false</c> and clear
    /// <c>adsolut_contact_id</c> (hard-delete catch-up). Active-flips
    /// without a fresh-set comparison are picked up in the regular pull
    /// path. Returns the count of links flipped.
    Task<int> ReconcileMissingLinksAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> presentAdsolutContactIds,
        CancellationToken ct = default);
}
