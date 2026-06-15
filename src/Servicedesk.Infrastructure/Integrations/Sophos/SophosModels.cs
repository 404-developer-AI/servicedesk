using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// One partner tenant as Sophos returns it, normalised for storage. The
/// <see cref="ShowAs"/> string carries the customer code as <c>[NNN] Name</c>;
/// <see cref="CompanyCode"/> is the parsed <c>NNN</c> (the bridge to
/// companies.adsolut_number). <see cref="ApiHost"/> is the region-specific base
/// required for the mailbox call and is absent on inactive/unprovisioned
/// tenants. Tenant id + api host are identifiers, not secrets.
public sealed class SophosTenantRow
{
    public string TenantId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ShowAs { get; set; }
    public string? CompanyCode { get; set; }
    public string? ApiHost { get; set; }
    public string? Status { get; set; }
    public string? DataRegion { get; set; }
    public string? BillingType { get; set; }

    /// True only when the tenant is active and has an api host — i.e. it can be
    /// queried for mailboxes.
    public bool IsQueryable =>
        !string.IsNullOrWhiteSpace(ApiHost)
        && string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
}

/// One protected (spam-filtered) mailbox address from a tenant. Only the
/// address is needed for matching; name/type are carried for display.
public sealed class SophosMailboxRow
{
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? MailboxType { get; set; }
}

/// A tenant to fetch mailboxes for (matched to an M365-connected company).
public sealed class SophosMailboxTarget
{
    public string TenantId { get; set; } = string.Empty;
    public string ApiHost { get; set; } = string.Empty;
}

/// Singleton global sync metadata. <see cref="ContentHash"/> lets a tick cheaply
/// detect "nothing changed" and bump only <see cref="LastCheckedUtc"/>.
public sealed class SophosSyncStateRow
{
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public int TenantCount { get; set; }
    public int MailboxCount { get; set; }
    public string? ContentHash { get; set; }
    public int? DurationMs { get; set; }
}

/// A tenant row joined with its matched company, for the settings tenant list.
public sealed class SophosTenantListRow
{
    public string TenantId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ShowAs { get; set; }
    public string? CompanyCode { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string? Status { get; set; }
    public bool M365Connected { get; set; }
    public int MailboxCount { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
}

/// Helpers for parsing the Sophos <c>showAs</c> string.
public static class SophosShowAs
{
    private static readonly Regex CodeRegex =
        new(@"^\s*\[\s*([^\]]+?)\s*\]\s*(.*)$", RegexOptions.Compiled);

    /// Parse <c>"[349] ACR Klimatechniek"</c> into code <c>"349"</c> and name
    /// <c>"ACR Klimatechniek"</c>. Returns a null code when there is no leading
    /// <c>[...]</c>; the name then falls back to the whole string.
    public static (string? Code, string Name) Parse(string? showAs)
    {
        var input = (showAs ?? string.Empty).Trim();
        if (input.Length == 0) return (null, string.Empty);

        var m = CodeRegex.Match(input);
        if (!m.Success) return (null, input);

        var code = m.Groups[1].Value.Trim();
        var name = m.Groups[2].Value.Trim();
        return (code.Length == 0 ? null : code, name.Length == 0 ? input : name);
    }
}
