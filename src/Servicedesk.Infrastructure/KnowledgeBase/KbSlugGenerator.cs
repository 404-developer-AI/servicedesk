using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.KnowledgeBase;

/// Pure helper that derives a URL-safe slug from a free-form title.
/// Lowercases, strips diacritics, collapses non-alphanumerics to single
/// hyphens, trims edges, and truncates to <see cref="MaxLength"/>. Output
/// matches the database CHECK regex `^[a-z0-9]+(-[a-z0-9]+)*$` for any
/// non-empty input, and falls back to <see cref="Fallback"/> when the
/// title contains nothing that can be mapped (e.g. only emoji).
public static partial class KbSlugGenerator
{
    public const int MaxLength = 80;
    public const string Fallback = "untitled";

    public static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Fallback;

        // Strip diacritics by decomposing to NFD and dropping combining marks
        // (é → e, ñ → n). Non-Latin scripts (Cyrillic, CJK) collapse to "" and
        // fall through to the Fallback so we never emit a slug that doesn't
        // round-trip via URL.
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }
        var ascii = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // Anything that isn't a-z or 0-9 becomes a hyphen, then runs of
        // hyphens collapse, then trim edges.
        var hyphenated = NonAlphaNum().Replace(ascii, "-");
        var collapsed = HyphenRun().Replace(hyphenated, "-").Trim('-');

        if (collapsed.Length == 0) return Fallback;
        if (collapsed.Length > MaxLength) collapsed = collapsed[..MaxLength].TrimEnd('-');
        return collapsed.Length == 0 ? Fallback : collapsed;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNum();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenRun();
}
