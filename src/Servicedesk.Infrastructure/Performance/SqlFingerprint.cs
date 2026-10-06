using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Performance;

/// Normalises SQL text into a stable "query shape" and a short hash.
///
/// Literals (strings, numbers, dollar-quoted bodies), comments and
/// whitespace runs are folded away, and expanded parameter/literal lists
/// (<c>IN (@ids1, @ids2, …)</c>, <c>VALUES (…), (…)</c>) collapse to one
/// form — so the same statement always maps to the same fingerprint no
/// matter its values, and no value ever reaches a metric row or an export.
/// Parameter <em>names</em> (<c>@TicketId</c>, <c>$1</c>) are kept: they are
/// code, not data, and make the shape readable.
public static partial class SqlFingerprint
{
    public const int MaxSqlLength = 4000;
    private const int CacheLimit = 5000;

    private static readonly ConcurrentDictionary<string, (string Fingerprint, string Normalized)> Cache = new(StringComparer.Ordinal);

    /// Fingerprint + normalised text, cached per distinct raw text (the data
    /// layer sends the same parameterised strings over and over).
    public static (string Fingerprint, string Normalized) Get(string sql)
    {
        if (Cache.TryGetValue(sql, out var hit)) return hit;
        var normalized = Normalize(sql);
        var result = (Hash(normalized), normalized);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache.TryAdd(sql, result);
        return result;
    }

    public static string Hash(string normalized)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()), digest);
        return Convert.ToHexString(digest[..6]).ToLowerInvariant();
    }

    public static string Normalize(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;
        var s = sql;
        s = BlockComment().Replace(s, " ");
        s = LineComment().Replace(s, " ");
        s = DollarQuoted().Replace(s, "?");
        s = EscapeString().Replace(s, "?");
        s = StringLiteral().Replace(s, "?");
        s = NumberLiteral().Replace(s, "?");
        s = Whitespace().Replace(s, " ").Trim();
        s = ParamList().Replace(s, "(...)");
        s = PlaceholderList().Replace(s, "(...)");
        s = ValuesList().Replace(s, "VALUES (...)");
        if (s.Length > MaxSqlLength) s = s[..MaxSqlLength] + " …";
        return s;
    }

    /// Converts named Dapper parameters (@Name) to positional $n placeholders
    /// so a captured shape can be fed to EXPLAIN (GENERIC_PLAN).
    public static string ToPositional(string normalized)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return NamedParam().Replace(normalized, m =>
        {
            var name = m.Groups[1].Value;
            if (!map.TryGetValue(name, out var n))
            {
                n = map.Count + 1;
                map[name] = n;
            }
            return m.Groups[0].Value[0] == '@' ? "$" + n : m.Value;
        });
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    // The tag group must always participate (empty alternative): in .NET a
    // back-reference to a group that did not match never matches, which would
    // leave plain $$…$$ bodies untouched.
    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*|)\$.*?\$\1\$", RegexOptions.Singleline)]
    private static partial Regex DollarQuoted();

    [GeneratedRegex(@"\b[Ee]'(?:[^'\\]|\\.|'')*'")]
    private static partial Regex EscapeString();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    // Numbers not glued to an identifier or a $n / @p placeholder.
    [GeneratedRegex(@"(?<![\w$@.])-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?(?![\w])")]
    private static partial Regex NumberLiteral();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Dapper list expansion: (@ids1, @ids2, @ids3) → (...)
    [GeneratedRegex(@"\(\s*@[A-Za-z_][A-Za-z0-9_]*?\d+(?:\s*,\s*@[A-Za-z_][A-Za-z0-9_]*?\d+)+\s*\)")]
    private static partial Regex ParamList();

    // (?, ?, ?) or ($1, $2, $3) → (...)
    [GeneratedRegex(@"\(\s*(?:\?|\$\d+)(?:\s*,\s*(?:\?|\$\d+))+\s*\)")]
    private static partial Regex PlaceholderList();

    // VALUES (...), (...), (...) → VALUES (...)
    [GeneratedRegex(@"VALUES\s*\((?:[^()]|\([^()]*\))*\)(?:\s*,\s*\((?:[^()]|\([^()]*\))*\))+", RegexOptions.IgnoreCase)]
    private static partial Regex ValuesList();

    [GeneratedRegex(@"(?<![\w@:])[@]([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex NamedParam();
}
