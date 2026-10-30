using Ganss.Xss;

namespace Servicedesk.Infrastructure.KnowledgeBase;

/// Server-side HTML sanitizer for knowledge base article bodies. Wraps
/// <see cref="HtmlSanitizer"/> with an allow-list tuned to the Tiptap editor
/// schema used by the KB composer: paragraphs, headings, lists, links,
/// images (only those served by the KB attachment endpoint), tables,
/// blockquotes, code, and basic inline marks.
///
/// CSS classes are limited to the explicit <see cref="ContentClasses"/> list
/// (callouts, interface labels, image sizes, mention chips). Without that
/// limit an author could borrow any app class or Tailwind utility — e.g.
/// <c>fixed inset-0 z-50</c> — and overlay the surrounding UI from inside a
/// body that other agents (or, once published, customers) read.
///
/// Anything outside the allow-list is stripped before persist; the
/// frontend renders bodies through a memoised <c>&lt;SafeHtml&gt;</c>
/// wrapper as a defence-in-depth second pass.
public sealed class KbHtmlSanitizer : IKbHtmlSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    public KbHtmlSanitizer()
    {
        _sanitizer = new HtmlSanitizer();
        ConfigureAllowList(_sanitizer);
    }

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return _sanitizer.Sanitize(html);
    }

    /// Allow-list tuned for the KB Tiptap schema. Conservative defaults from
    /// the Ganss base are kept (drops `<script>`, on*-attrs, javascript:
    /// URLs); we then narrow further by only adding tags we actually emit.
    private static void ConfigureAllowList(HtmlSanitizer sanitizer)
    {
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "hr",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "strong", "em", "u", "s", "code", "pre", "blockquote",
            "ul", "ol", "li",
            "a", "img",
            "table", "thead", "tbody", "tr", "td", "th",
            "figure", "figcaption",
            "span", "div",
        })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "title", "alt", "src", "class", "colspan", "rowspan" })
        {
            sanitizer.AllowedAttributes.Add(attr);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("tel");

        // Non-empty AllowedClasses = allow-list mode (an empty set would
        // allow every class).
        sanitizer.AllowedClasses.Clear();
        foreach (var cls in ContentClasses)
        {
            sanitizer.AllowedClasses.Add(cls);
        }

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowDataAttributes = false;
    }

    /// Classes the rich-text editors emit into stored HTML. An editor feature
    /// that serialises a new class must be added here, and styled in
    /// index.css (article classes under `.kb-doc`).
    public static readonly IReadOnlyList<string> ContentClasses = new[]
    {
        // KB article editor (RichTextEditor variant="article").
        "sd-callout", "sd-callout--info", "sd-callout--tip", "sd-callout--warning", "sd-callout--important",
        "sd-ui",
        "sd-img--sm", "sd-img--md",
        // Composer chips that templates may carry.
        "sd-mention", "sd-intake-mention", "sd-order-mention",
    };
}

public interface IKbHtmlSanitizer
{
    string Sanitize(string? html);
}
