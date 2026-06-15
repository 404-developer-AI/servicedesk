namespace Servicedesk.Infrastructure.Integrations.Veeam;

/// The resolved connection to one Veeam Service Provider Console (VSPC): base
/// URL + login (password from protected_secrets) and whether a self-signed TLS
/// certificate is accepted. Built fresh from settings/secrets per pass so the
/// API client stays settings-agnostic.
public sealed record VeeamConnection(string BaseUrl, string Username, string Password, bool AllowSelfSigned);

/// Outcome of listing the VSPC companies. <see cref="AuthFailure"/> means the
/// credentials were rejected (bad URL/user/password or a 401/403) — the sync
/// surfaces "credentials rejected" rather than retrying blindly. A non-auth
/// failure is transient (network / throttle / 5xx) and the next tick retries.
public sealed record VeeamCompaniesResult(
    bool Success, bool AuthFailure, string? Error, IReadOnlyList<VeeamCompanyRow> Companies)
{
    public static VeeamCompaniesResult Ok(IReadOnlyList<VeeamCompanyRow> companies) =>
        new(true, false, null, companies);
    public static VeeamCompaniesResult Auth(string error) =>
        new(false, true, error, Array.Empty<VeeamCompanyRow>());
    public static VeeamCompaniesResult Transient(string error) =>
        new(false, false, error, Array.Empty<VeeamCompanyRow>());
}

/// Outcome of listing one company's VB365 protected objects.
public sealed record VeeamProtectedObjectsResult(
    bool Success, bool AuthFailure, string? Error, IReadOnlyList<VeeamProtectedObjectRow> Objects)
{
    public static VeeamProtectedObjectsResult Ok(IReadOnlyList<VeeamProtectedObjectRow> rows) =>
        new(true, false, null, rows);
    public static VeeamProtectedObjectsResult Auth(string error) =>
        new(false, true, error, Array.Empty<VeeamProtectedObjectRow>());
    public static VeeamProtectedObjectsResult Transient(string error) =>
        new(false, false, error, Array.Empty<VeeamProtectedObjectRow>());
}

/// Outcome of the credential probe (Settings → test connection). Never stores
/// anything; validates the password grant and counts the visible companies.
public sealed record VeeamProbeResult(bool Success, string? Error, int CompanyCount);

/// One VSPC company. <see cref="Code"/> is <c>ownerCredentials.userName</c> —
/// the relation code that bridges to companies.adsolut_number (same bridge the
/// M365 / Sophos matching uses). <see cref="InstanceUid"/> is the company uid
/// used as the organizationFilter for the protected-objects call.
public sealed class VeeamCompanyRow
{
    public string InstanceUid { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Code { get; set; }
}

/// One VB365 protected object as VSPC returns it. <see cref="Name"/> is the
/// display name — the only per-user identity VSPC exposes (no email / UPN /
/// Entra id), so it is the join key to an M365 mailbox. <see cref="RepositoryName"/>
/// says which service the object is (Exchange / OneDrive).
public sealed class VeeamProtectedObjectRow
{
    public string? Name { get; set; }
    public string? RepositoryName { get; set; }
    public int RestorePointsCount { get; set; }
    public DateTime? LatestRestorePointDate { get; set; }

    /// True when this object's repository is an Exchange mailbox repository.
    public bool IsExchange =>
        RepositoryName?.Contains("Exchange", StringComparison.OrdinalIgnoreCase) == true;

    /// True when this object's repository is a OneDrive repository.
    public bool IsOneDrive =>
        RepositoryName?.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) == true;
}

/// A company to read VSPC backup status for: an M365-connected company with a
/// relation code that can be matched to a VSPC company.
public sealed class VeeamSyncTarget
{
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
}

/// One mailbox's backup status for a company, keyed by the normalized display
/// name (the VSPC join key). Stored in veeam_backups and read back to overlay
/// the M365 mailbox table with the OneDrive / Exchange Protected pills.
public sealed class VeeamBackupRow
{
    public string MatchKey { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool ExchangeProtected { get; set; }
    public int? ExchangeRestorePoints { get; set; }
    public DateTime? ExchangeLastBackupUtc { get; set; }
    public bool OneDriveProtected { get; set; }
    public int? OneDriveRestorePoints { get; set; }
    public DateTime? OneDriveLastBackupUtc { get; set; }
}

/// Singleton global sync metadata. <see cref="ContentHash"/> lets a tick cheaply
/// detect "nothing changed" and bump only <see cref="LastCheckedUtc"/>.
public sealed class VeeamSyncStateRow
{
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public int CompanyCount { get; set; }
    public int ObjectCount { get; set; }
    public string? ContentHash { get; set; }
    public int? DurationMs { get; set; }
}

/// A matched company joined with its VSPC link, for the settings company list.
public sealed class VeeamCompanyListRow
{
    public Guid CompanyId { get; set; }
    public string? CompanyCode { get; set; }
    public string? CompanyName { get; set; }
    public string? VspcCompanyName { get; set; }
    public int ObjectCount { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
}

/// Helpers for normalizing the display name used as the VSPC ↔ M365 join key.
public static class VeeamMatch
{
    /// Lower-cased, trimmed display name. Returns null for an empty/whitespace
    /// name so it never becomes a join key.
    public static string? Key(string? displayName)
    {
        var v = displayName?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.ToLowerInvariant();
    }
}
