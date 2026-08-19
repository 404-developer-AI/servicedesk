using Ganss.Xss;

namespace Servicedesk.Infrastructure.Portal;

/// Server-side sanitizer for HTML a customer submits from the portal
/// (new-ticket description, replies). Narrow allow-list tuned to the
/// portal's reduced Tiptap toolbar: paragraphs, headings, lists, links,
/// quotes, code, basic inline marks. No images (files travel as
/// attachments, never inline), no tables, no styles, no data-* attributes.
/// The agent UI renders it through DOMPurify again as a second pass.
public static class PortalHtmlSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = Build();

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return Sanitizer.Sanitize(html).Trim();
    }

    private static HtmlSanitizer Build()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "h1", "h2", "h3", "strong", "b", "em", "i", "u", "s",
            "code", "pre", "blockquote", "ul", "ol", "li", "a",
        })
            s.AllowedTags.Add(tag);

        s.AllowedAttributes.Clear();
        s.AllowedAttributes.Add("href");
        s.AllowedAttributes.Add("title");

        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");

        s.AllowedCssProperties.Clear();
        s.AllowedAtRules.Clear();
        s.AllowDataAttributes = false;
        s.KeepChildNodes = true;

        // Outbound links always open safely.
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement el && el.LocalName == "a")
            {
                el.SetAttribute("rel", "noopener noreferrer nofollow");
                el.SetAttribute("target", "_blank");
            }
        };
        return s;
    }
}
