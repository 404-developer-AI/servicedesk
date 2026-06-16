using Servicedesk.Infrastructure.Integrations.Sophos;
using Servicedesk.Infrastructure.Integrations.Veeam;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.M365;

/// One synced mailbox enriched with the Sophos spam-filter verdict and the
/// Veeam backup status. The nullable protection flags are null when the
/// matching integration is off or the company is not known to it (distinct
/// from "known but unprotected" = false), so the UI / report can render an
/// honest "n/a" instead of a misleading "unprotected".
public sealed class M365EnrichedMailbox
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

    public bool? SpamFilterProtected { get; set; }

    public bool? ExchangeProtected { get; set; }
    public int? ExchangeRestorePoints { get; set; }
    public DateTime? ExchangeLastBackupUtc { get; set; }

    public bool? OneDriveProtected { get; set; }
    public int? OneDriveRestorePoints { get; set; }
    public DateTime? OneDriveLastBackupUtc { get; set; }
}

/// The full per-company mailbox view: company label + connection/sync meta +
/// the availability flags + the enriched mailbox rows. Shared by the matching
/// detail endpoint and the report sender so the Sophos/Veeam overlay logic
/// lives in exactly one place.
public sealed class M365CompanyMailboxView
{
    public Guid CompanyId { get; set; }
    public string? CompanyCode { get; set; }
    public string? CompanyName { get; set; }
    public string? Status { get; set; }
    public string? TenantId { get; set; }
    public DateTime? ConsentedAt { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }
    public string? LastError { get; set; }
    public bool SpamFilterAvailable { get; set; }
    public bool VeeamAvailable { get; set; }
    public IReadOnlyList<M365EnrichedMailbox> Mailboxes { get; set; } = Array.Empty<M365EnrichedMailbox>();
}

public interface IM365MatchingService
{
    /// Builds the enriched mailbox view for one company: the synced mailboxes
    /// overlaid with the Sophos spam-filter verdict (when the integration is on
    /// and a Sophos tenant maps to this company) and the Veeam backup status
    /// (when on and the company is known in VSPC).
    Task<M365CompanyMailboxView> GetCompanyViewAsync(Guid companyId, CancellationToken ct);
}

public sealed class M365MatchingService : IM365MatchingService
{
    private readonly IM365TenantStore _store;
    private readonly ISophosStore _sophos;
    private readonly IVeeamStore _veeam;
    private readonly ISettingsService _settings;

    public M365MatchingService(
        IM365TenantStore store,
        ISophosStore sophos,
        IVeeamStore veeam,
        ISettingsService settings)
    {
        _store = store;
        _sophos = sophos;
        _veeam = veeam;
        _settings = settings;
    }

    public async Task<M365CompanyMailboxView> GetCompanyViewAsync(Guid companyId, CancellationToken ct)
    {
        var label = await _store.GetCompanyLabelAsync(companyId, ct);
        var link = await _store.GetLinkAsync(companyId, ct);
        var syncState = await _store.GetSyncStateAsync(companyId, ct);
        var mailboxes = await _store.GetMailboxesAsync(companyId, ct);

        // Sophos spam-filter overlay. Only computed when the integration is on
        // and a Sophos tenant maps to this company.
        var sophosEnabled = await _settings.GetAsync<bool>(SettingKeys.Sophos.Enabled, ct);
        var spamFilterAvailable = false;
        HashSet<string> protectedEmails = new(StringComparer.OrdinalIgnoreCase);
        if (sophosEnabled)
        {
            var (tenantMatched, emails) = await _sophos.GetCompanySpamFilterAsync(companyId, ct);
            spamFilterAvailable = tenantMatched;
            protectedEmails = emails;
        }

        bool? ProtectedFor(string? mail, string? upn)
        {
            if (!spamFilterAvailable) return null;
            if (!string.IsNullOrWhiteSpace(mail) && protectedEmails.Contains(mail.Trim())) return true;
            if (!string.IsNullOrWhiteSpace(upn) && protectedEmails.Contains(upn.Trim())) return true;
            return false;
        }

        // Veeam backup overlay. Only computed when the integration is on and this
        // company is known in VSPC. Joined per mailbox on the normalized display
        // name (the only identity VSPC exposes — no email / UPN / Entra id).
        var veeamEnabled = await _settings.GetAsync<bool>(SettingKeys.Veeam.Enabled, ct);
        var veeamAvailable = false;
        Dictionary<string, VeeamBackupRow> backupsByName = new(StringComparer.Ordinal);
        if (veeamEnabled)
        {
            var (known, byName) = await _veeam.GetCompanyBackupsAsync(companyId, ct);
            veeamAvailable = known;
            backupsByName = byName;
        }

        VeeamBackupRow? BackupFor(string? displayName)
        {
            if (!veeamAvailable) return null;
            var key = VeeamMatch.Key(displayName);
            return key is not null && backupsByName.TryGetValue(key, out var row) ? row : null;
        }

        var enriched = mailboxes.Select(m =>
        {
            var backup = BackupFor(m.DisplayName);
            return new M365EnrichedMailbox
            {
                ObjectId = m.ObjectId,
                MailboxType = m.MailboxType,
                DisplayName = m.DisplayName,
                GivenName = m.GivenName,
                Surname = m.Surname,
                Upn = m.Upn,
                Mail = m.Mail,
                Enabled = m.Enabled,
                Licenses = m.Licenses,
                SpamFilterProtected = ProtectedFor(m.Mail, m.Upn),
                ExchangeProtected = veeamAvailable ? backup?.ExchangeProtected ?? false : (bool?)null,
                ExchangeRestorePoints = backup?.ExchangeRestorePoints,
                ExchangeLastBackupUtc = backup?.ExchangeLastBackupUtc,
                OneDriveProtected = veeamAvailable ? backup?.OneDriveProtected ?? false : (bool?)null,
                OneDriveRestorePoints = backup?.OneDriveRestorePoints,
                OneDriveLastBackupUtc = backup?.OneDriveLastBackupUtc,
            };
        }).ToList();

        return new M365CompanyMailboxView
        {
            CompanyId = companyId,
            CompanyCode = label?.Code,
            CompanyName = label?.Name,
            Status = link?.Status,
            TenantId = link?.TenantId,
            ConsentedAt = link?.ConsentedAt,
            LastCheckedUtc = syncState?.LastCheckedUtc,
            LastChangedUtc = syncState?.LastChangedUtc,
            LastError = link?.LastError ?? syncState?.LastError,
            SpamFilterAvailable = spamFilterAvailable,
            VeeamAvailable = veeamAvailable,
            Mailboxes = enriched,
        };
    }
}
