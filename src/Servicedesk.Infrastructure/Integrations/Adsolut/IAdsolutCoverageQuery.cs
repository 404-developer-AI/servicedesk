namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.30 — surface the gaps between SD's local universe and Adsolut.
/// Each bucket counts rows that exist locally but are not (or no longer)
/// reflected upstream. Counts are raw — no toggle-conditional masking
/// applied here. Callers (the tile UI, the sync worker's tick-summary)
/// decide what to render based on the current toggle state.
///
/// Bucket definitions:
///   • <see cref="CompaniesSdOnly"/>: <c>companies.adsolut_id IS NULL</c>
///     and <c>is_active = TRUE</c>. Rows that never linked to Adsolut
///     (yet) — either local-only by design, or candidates a future
///     create-push would mint upstream.
///   • <see cref="CompaniesDrift"/>: linked + <c>updated_utc &gt;
///     adsolut_last_modified</c>. Rows where SD has a newer state than
///     the last pull saw.
///   • <see cref="ContactLinksUnsynced"/>: <c>contact_companies.adsolut_contact_id
///     IS NULL</c> with the parent company linked to Adsolut. Local-only
///     contact-links waiting for a create-push.
///   • <see cref="ContactLinksDrift"/>: linked + <c>contacts.updated_utc
///     &gt; cc.adsolut_last_modified</c>. Per-link drift.
///   • <see cref="ContactsPureSd"/>: contacts that have never had any
///     Adsolut-aware link (no <c>adsolut_active</c> stamped on any link).
///     Pure local contacts that will never appear in Adsolut unless an
///     admin links them to an Adsolut-linked company.
/// Sealed class with public get/set props (not a positional record) so
/// Dapper does property-based binding instead of constructor-signature
/// matching. The SQL casts <c>COUNT(*)</c> (bigint) to <c>::int</c> so a
/// missed cast can't bring back the Int64-vs-Int32 ctor-mismatch the
/// positional-record shape produced before — see Adsolut.md → v0.0.30
/// post-deploy fix.
public sealed class AdsolutCoverageCounts
{
    public int CompaniesSdOnly { get; set; }
    public int CompaniesDrift { get; set; }
    public int ContactLinksUnsynced { get; set; }
    public int ContactLinksDrift { get; set; }
    public int ContactsPureSd { get; set; }
}

/// One row in the companies-coverage list. Flat by design: keeps the
/// admin's overview-page render path one keyset/offset sweep + one map.
public class AdsolutCoverageCompanyRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Email { get; set; }
    public Guid? AdsolutId { get; set; }
    public string? AdsolutNumber { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// One row in the contact-links / contacts coverage list. <see cref="LinkId"/>
/// is the <c>contact_companies.id</c> for link-buckets;
/// <see cref="ContactId"/> is the underlying contact for the pure-SD bucket
/// (the row's <c>LinkId</c> is then the primary link's id, or null when
/// the contact has no links at all). The view distinguishes by bucket so
/// the action surface knows what to mutate.
public class AdsolutCoverageContactRow
{
    public Guid? LinkId { get; set; }
    public Guid ContactId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public Guid? CompanyAdsolutId { get; set; }
    public Guid? AdsolutContactId { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime ContactUpdatedUtc { get; set; }
}

/// Paged-result envelope used by both list-endpoints. <see cref="Total"/>
/// is computed via <c>COUNT(*) OVER ()</c> on the same query so the page
/// + total come back in one round-trip.
public sealed record AdsolutCoveragePage<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize);

public enum AdsolutCoverageCompaniesBucket
{
    SdOnly,
    Drift,
}

public enum AdsolutCoverageContactsBucket
{
    LinksUnsynced,
    LinksDrift,
    PureSd,
}

public interface IAdsolutCoverageQuery
{
    /// One round-trip that fills the tile. Five raw counts; the tile
    /// applies its own toggle-conditional masking (drift counts are
    /// suppressed when the relevant push-toggle is ON because the
    /// push-tak will resolve them within one tick).
    Task<AdsolutCoverageCounts> GetCountsAsync(CancellationToken ct = default);

    Task<AdsolutCoveragePage<AdsolutCoverageCompanyRow>> ListCompaniesAsync(
        AdsolutCoverageCompaniesBucket bucket,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task<AdsolutCoveragePage<AdsolutCoverageContactRow>> ListContactsAsync(
        AdsolutCoverageContactsBucket bucket,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default);
}
