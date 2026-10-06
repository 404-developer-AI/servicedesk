using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Performance;

/// Last line of defence before anything leaves the server in an export or a
/// dashboard response. Metric keys are already value-free by construction
/// (route templates, normalised SQL), but query text read from
/// pg_stat_statements, worker error kinds and client-reported routes are
/// passed through here as well, so a literal that slipped through (a
/// hand-written SQL constant, an email in an exception type name, an id in
/// a client route) is masked rather than exported.
public static partial class PerfRedactor
{
    /// SQL: normalise (literals → ?), then mask anything identifying that
    /// could still be embedded in an identifier-looking token.
    public static string Sql(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;
        return Text(SqlFingerprint.Normalize(sql));
    }

    /// Free text: emails, GUIDs, IP addresses and bearer/token-looking
    /// strings are replaced by placeholders. <paramref name="maskNumbers"/>
    /// also masks runs of 3+ digits (ids, ticket numbers) — used for
    /// admin-typed text; off for generated text, whose numbers are metrics.
    public static string Text(string? text, bool maskNumbers = false)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = Email().Replace(text, "{email}");
        s = Guid().Replace(s, "{guid}");
        s = Ipv4().Replace(s, "{ip}");
        s = Ipv6().Replace(s, "{ip}");
        s = Token().Replace(s, "{token}");
        if (maskNumbers) s = Digits().Replace(s, "{n}");
        return s;
    }

    /// Client-reported route/path: keep only path-safe characters, replace
    /// id-like segments, cap the length. Returns null when nothing sane is left.
    public static string? Route(string? route, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;
        var s = route.Trim();
        var q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q];
        if (s.Length > 400) s = s[..400];
        if (!RouteChars().IsMatch(s)) return null;
        var segments = s.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0 || seg.StartsWith('$') || seg.StartsWith('{')) continue;
            if (Guid().IsMatch(seg) || AllDigits().IsMatch(seg) || seg.Length > 40 || Email().IsMatch(seg) || HexId().IsMatch(seg))
                segments[i] = "{id}";
        }
        s = string.Join('/', segments);
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){4,7}[0-9a-fA-F]{1,4}\b")]
    private static partial Regex Ipv6();

    // JWT-ish, or a long mixed letter+digit run without underscores (keys,
    // tokens) — long snake_case identifiers such as index names stay intact.
    [GeneratedRegex(@"\b(?:eyJ[A-Za-z0-9_-]{10,}|(?=[A-Za-z0-9\-]*\d)(?=[A-Za-z0-9\-]*[A-Za-z])[A-Za-z0-9\-]{32,})\b")]
    private static partial Regex Token();

    [GeneratedRegex(@"\b\d{3,}\b")]
    private static partial Regex Digits();

    // '@' is accepted so an e-mail address in a path can be recognised and
    // replaced by {id} below rather than rejecting the whole route.
    [GeneratedRegex(@"^[A-Za-z0-9/_\-.$:{}*@]+$")]
    private static partial Regex RouteChars();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex AllDigits();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,}$")]
    private static partial Regex HexId();
}
