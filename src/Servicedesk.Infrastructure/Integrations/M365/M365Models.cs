namespace Servicedesk.Infrastructure.Integrations.M365;

/// Link lifecycle states (mirrors the <c>m365_tenant_links.status</c> column).
public static class M365LinkStatus
{
    /// Consent granted; reads are expected to work.
    public const string Connected = "connected";

    /// A read failed with 401/403 (consent revoked or a new permission added) —
    /// the customer admin must grant consent again.
    public const string NeedsReconsent = "needs_reconsent";

    /// A non-auth read error (network, throttling, Graph 5xx). Transient; the
    /// next tick retries without forcing re-consent.
    public const string Error = "error";
}

/// A pending consent round-trip, minted by the connect endpoint and consumed
/// once by the public callback. The state token is the entire trust model of
/// that public callback, so it is single-use and server-time-boxed.
public sealed class M365ConsentStateRow
{
    public Guid CompanyId { get; set; }
    public Guid? InitiatedBy { get; set; }
}

/// One per company. <see cref="TenantId"/> is the customer's directory id
/// returned by Azure on consent — an identifier, not a secret.
public sealed class M365TenantLinkRow
{
    public Guid CompanyId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Status { get; set; } = M365LinkStatus.Connected;
    public DateTime? ConsentedAt { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
    public string? LastError { get; set; }
}

/// A synced mailbox — the C# shape of one row the probe script produced.
public sealed class M365MailboxRow
{
    public string ObjectId { get; set; } = string.Empty;
    public string? MailboxType { get; set; }
    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? Surname { get; set; }
    public string? Upn { get; set; }
    public string? Mail { get; set; }
    public bool? Enabled { get; set; }
    public string? Licenses { get; set; }
}

/// Per-company sync metadata. <see cref="ContentHash"/> lets a tick cheaply
/// detect "nothing changed" and bump only <see cref="LastCheckedUtc"/>.
public sealed class M365CompanySyncRow
{
    public Guid CompanyId { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public int MailboxCount { get; set; }
    public string? ContentHash { get; set; }
    public int? DurationMs { get; set; }
}

/// The company's display label for the M365 detail header. Code is the Adsolut
/// relation number (companies.adsolut_number), matching the matching list.
public sealed class M365CompanyLabel
{
    public string? Code { get; set; }
    public string? Name { get; set; }
}

/// Link + sync meta joined for the matching list (one row per connected
/// company). Drives the per-company status badge and "last synced" copy.
public sealed class M365ConnectionRow
{
    public Guid CompanyId { get; set; }
    public string? TenantId { get; set; }
    public string Status { get; set; } = M365LinkStatus.Connected;
    public DateTime? ConsentedAt { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }
    public int MailboxCount { get; set; }
    public string? LastError { get; set; }
}
