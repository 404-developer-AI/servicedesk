namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One <c>contact_companies</c> row read from the local database, joined
/// with the host <c>contacts</c> + parent <c>companies</c> rows for the
/// fields the v0.0.29 push needs. The pusher iterates per-link, not
/// per-contact: Adsolut models one contact-row per work-relationship, so
/// the SD-side equivalent is a link, and a SD edit on a contact whose
/// person is linked to three customers fans out to three PUTs.
///
/// Hard rule (locked-in): SD → Adsolut never pushes a contact without a
/// linked Adsolut <c>company_id</c>. The candidate query already filters
/// on <c>companies.adsolut_id IS NOT NULL</c>; this struct surfaces it
/// for the audit log and the SQL update statement.
public sealed class AdsolutContactPushCandidate
{
    /// <c>contact_companies.id</c> — the link primary key. Used to
    /// stamp <c>adsolut_synced_hash</c> + <c>adsolut_last_modified</c>
    /// after a successful PUT/POST.
    public Guid LinkId { get; set; }

    /// <c>contacts.id</c> — diagnostic only, never sent upstream.
    public Guid ContactId { get; set; }

    /// <c>companies.id</c> — diagnostic only.
    public Guid CompanyId { get; set; }

    /// <c>companies.adsolut_id</c> — required (NOT NULL by the candidate
    /// SELECT). Path component on every customer-contact endpoint.
    public Guid CompanyAdsolutId { get; set; }

    /// <c>contact_companies.adsolut_contact_id</c>. NULL → CREATE branch
    /// (POST /customers/{customer}/contacts); non-null → UPDATE branch
    /// (PUT /…/contacts/{this}).
    public Guid? AdsolutContactId { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string MobilePhone { get; set; } = string.Empty;

    /// Aggregate row-level <c>contacts.updated_utc</c>. Drives the
    /// per-link drift-vs-no-drift gate.
    public DateTime ContactUpdatedUtc { get; set; }

    /// Per-link <c>contact_companies.adsolut_last_modified</c>. NULL for
    /// links that have never been pushed and never been pulled (CREATE
    /// branch). Used as the per-link drift comparator on the UPDATE branch.
    public DateTime? AdsolutLastModified { get; set; }

    /// Per-link <c>contact_companies.adsolut_synced_hash</c>. NULL on
    /// upgrade-from-v0.0.27 rows that were never pulled with the v0.0.28
    /// hash column populated; treat-as-dirty so the first push fires once
    /// and stamps the hash.
    public byte[]? AdsolutSyncedHash { get; set; }
}

/// Toggles + capacity for one push-tick of contacts. Snapshot at the
/// start of the tick so a settings-edit mid-tick can't change behaviour
/// for the rows already partially processed. Mirrors
/// <see cref="AdsolutPushOptions"/> for symmetry.
public sealed record AdsolutContactsPushOptions(
    bool PushUpdateEnabled,
    bool PushCreateEnabled);

/// Outcome of a single contact-push attempt against one local link row.
public enum AdsolutContactPushOutcome
{
    /// Linked row was PUT to /customers/{customer}/contacts/{contact}.
    Updated,

    /// Unlinked row was POSTed and the response id was persisted on the
    /// local link.
    Created,

    /// Linked row hashed identically to the last-synced hash — no PUT
    /// fired (closes the echo-pull loop on the per-link state).
    SkippedNoChange,

    /// Update-toggle is OFF and the link needs an update.
    SkippedUpdateToggleOff,

    /// Create-toggle is OFF and the link would be a fresh POST.
    SkippedCreateToggleOff,

    /// Linked row's <c>contacts.updated_utc</c> is not strictly newer than
    /// the per-link <c>adsolut_last_modified</c>. Nothing to push — the
    /// link is already up-to-date per timestamp.
    SkippedNoLocalChange,

    /// SD-side contact has no email. v0.0.28 pull-side already audits
    /// these as "missing email"; the push-side cannot meaningfully
    /// represent a contact without an email either, so we skip with a
    /// dedicated outcome so the operational log stays honest.
    SkippedNoEmail,
}

public sealed record AdsolutContactPushDecision(AdsolutContactPushOutcome Outcome);

/// Outbound counterpart to <see cref="IAdsolutContactUpserter"/>. One
/// method processes a single candidate: build the canonical hash, run the
/// pure decision, call the write-client, persist the result on the local
/// link row.
public interface IAdsolutContactPusher
{
    Task<AdsolutContactPushOutcome> PushOneAsync(
        Guid administrationId,
        AdsolutContactPushCandidate candidate,
        AdsolutContactsPushOptions options,
        CancellationToken ct = default);

    /// Loads the candidate set from <c>contact_companies</c> joined with
    /// <c>contacts</c> + <c>companies</c>. Active rows only. Linked links
    /// where <c>contacts.updated_utc &gt; adsolut_last_modified</c>
    /// qualify for update; unlinked links with a known
    /// <c>companies.adsolut_id</c> qualify for create. The caller filters
    /// on the toggles before calling <see cref="PushOneAsync"/>.
    Task<IReadOnlyList<AdsolutContactPushCandidate>> LoadCandidatesAsync(
        AdsolutContactsPushOptions options,
        int limit,
        CancellationToken ct = default);

    /// v0.0.30 — load a single link by <c>contact_companies.id</c> so the
    /// coverage page's "Force sync" action can target one specific row
    /// outside the per-tick cap. Returns null when the link is missing,
    /// soft-deleted, or its parent company is not linked to Adsolut.
    Task<AdsolutContactPushCandidate?> LoadCandidateByLinkIdAsync(
        Guid linkId,
        CancellationToken ct = default);
}
