using System.Net;
using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.KnowledgeBase;

/// Pure helper that converts a sanitized rich-text body to a plain-text
/// representation suitable for full-text search. The output of
/// <see cref="HtmlToText"/> is what gets persisted into
/// <c>kb_article_translations.body_text</c>; the database-side generated
/// <c>search_vector</c> tokenises that column.
///
/// Mirrors the regex-based stripping pattern used elsewhere in the codebase
/// (see <c>OutboundMailService.HtmlToText</c>) — no HTML parser dependency
/// for this read-only conversion.
public static partial class KbBodyStripper
{
    /// Convert a sanitized HTML body to plain text. Preserves paragraph
    /// boundaries as newlines so FTS tokens don't glue across blocks, and
    /// collapses runs of whitespace so adjacent block-level elements don't
    /// produce noisy gaps.
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // Drop script/style content entirely — anything inside should not
        // contribute to FTS or be rendered visually after sanitization, but
        // run defensively in case the sanitizer ever lets a wrapper through.
        var dropped = DropElements().Replace(html, " ");

        // Block-level boundaries become newlines so the text representation
        // keeps paragraph structure that to_tsvector treats as token gaps.
        var withBreaks = BlockBreak().Replace(dropped, "\n");

        // Strip remaining tags.
        var tagless = TagStrip().Replace(withBreaks, " ");

        // Decode &amp;, &nbsp;, &#39;, etc. — &nbsp; becomes U+00A0 which we
        // normalise alongside other whitespace below.
        var decoded = WebUtility.HtmlDecode(tagless);

        // Collapse whitespace runs but preserve newlines so paragraph breaks
        // survive to the FTS feed.
        var compacted = WhitespaceCollapse().Replace(decoded, m => m.Value.Contains('\n') ? "\n" : " ");

        return compacted.Trim();
    }

    [GeneratedRegex("<(script|style)\\b[^>]*>.*?</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DropElements();

    [GeneratedRegex("</(p|div|h[1-6]|li|tr|br|hr|blockquote|pre|figure|figcaption)\\s*>|<br\\s*/?\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]+>",
        RegexOptions.CultureInvariant)]
    private static partial Regex TagStrip();

    [GeneratedRegex("[\\s\\u00A0]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceCollapse();
}
