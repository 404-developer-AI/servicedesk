using System.Globalization;
using System.Net;

namespace Servicedesk.Api.Reporting;

/// IP allow-list matcher for the Reporting API (v0.0.96). The list is an
/// admin-edited setting: comma/semicolon/whitespace-separated plain IPs
/// and/or CIDR ranges, IPv4 and IPv6 mixed freely.
///
/// Fail-closed by construction: an empty list means "no IP restriction",
/// but once the list has entries anything that does not positively match —
/// including an unparseable entry, a missing caller address, or a family
/// mismatch — denies. Containment is computed by masking both sides, so a
/// CIDR base with host bits set (e.g. 192.168.1.5/24) still matches its
/// whole range instead of silently matching nothing.
public static class ReportingIpAllowList
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    public static bool IsAllowed(string? allowList, IPAddress? remote)
    {
        var entries = (allowList ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return true;
        if (remote is null) return false;

        var caller = Normalize(remote);
        foreach (var entry in entries)
        {
            if (Matches(entry, caller)) return true;
        }
        return false;
    }

    private static bool Matches(string entry, IPAddress caller)
    {
        var slash = entry.IndexOf('/');
        if (slash < 0)
        {
            return IPAddress.TryParse(entry, out var ip) && Normalize(ip).Equals(caller);
        }

        if (!IPAddress.TryParse(entry.AsSpan(0, slash), out var baseIp)) return false;
        if (!int.TryParse(entry.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)) return false;

        baseIp = Normalize(baseIp);
        if (baseIp.AddressFamily != caller.AddressFamily) return false;

        var baseBytes = baseIp.GetAddressBytes();
        if (prefix > baseBytes.Length * 8) return false;

        return InNetwork(baseBytes, prefix, caller.GetAddressBytes());
    }

    private static bool InNetwork(byte[] baseBytes, int prefix, byte[] callerBytes)
    {
        var fullBytes = prefix / 8;
        var remainderBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (baseBytes[i] != callerBytes[i]) return false;
        }
        if (remainderBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainderBits));
        return (baseBytes[fullBytes] & mask) == (callerBytes[fullBytes] & mask);
    }

    /// Nginx hands the app the real client address via forwarded headers,
    /// but on dual-stack sockets it can surface as an IPv4-mapped IPv6
    /// address (::ffff:203.0.113.10). Compare in canonical IPv4 form so an
    /// admin's plain-IPv4 entries match either representation.
    private static IPAddress Normalize(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
