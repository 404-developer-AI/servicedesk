using System.Net;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

/// Kind of suspicious behaviour observed from one client IP.
public enum IpSignalKind
{
    /// Request for a path only a vulnerability scanner asks for (/.env,
    /// /credentials.json, /wp-admin…). The only signal that auto-blocks.
    ScannerPath,
    RateLimited,
    CsrfRejected,
    FailedLogin,
}

public static class IpRuleKind
{
    public const string Block = "block";
    public const string Whitelist = "whitelist";
}

public static class IpProposalStatus
{
    public const string Open = "open";
    public const string Blocked = "blocked";
    public const string Whitelisted = "whitelisted";
    public const string Dismissed = "dismissed";
}

public sealed record IpRule(
    string Ip,
    string Kind,
    string Source,
    string? Reason,
    long? ProposalId,
    DateTime CreatedUtc,
    string CreatedBy,
    DateTime? ExpiresUtc,
    long HitCount,
    DateTime? LastHitUtc);

public sealed record IpProposal(
    long Id,
    string Ip,
    string Status,
    bool AutoBlocked,
    IReadOnlyList<string> Reasons,
    string EvidenceJson,
    bool KnownLogin,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    DateTime CreatedUtc,
    DateTime? DecidedUtc,
    string? DecidedBy);

/// Snapshot of one IP's signals in the current window, handed from the
/// in-memory detector to the worker that persists proposals.
public sealed record IpThreatObservation(
    string Ip,
    IReadOnlyDictionary<IpSignalKind, int> Counts,
    IReadOnlyList<string> SamplePaths,
    IReadOnlyList<IpSignalKind> TrippedKinds,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

public static class IpAddressText
{
    /// Canonical text form used as the key everywhere (rules, proposals,
    /// in-memory sets): IPv4-mapped IPv6 ("::ffff:1.2.3.4") collapses to
    /// "1.2.3.4" so one client never has two identities.
    public static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    /// Parses admin/console input; null when it is not a single IP address
    /// (ranges/CIDR are deliberately not supported).
    public static string? TryNormalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return IPAddress.TryParse(text.Trim(), out var ip) ? Normalize(ip) : null;
    }
}
