using Ganss.Xss;

namespace Servicedesk.Infrastructure.Signatures;

/// Sanitizes the rich-text in signature text/disclaimer blocks. Distinct from
/// the KB sanitizer because a signature legitimately carries inline styling
/// (colour, size, weight, alignment) the builder applies — but the property
/// allow-list is kept tight and deliberately excludes any url()-bearing
/// property (e.g. background-image) so a signature can never fetch an external
/// resource or smuggle a tracking pixel. The Ganss base still strips
/// <c>&lt;script&gt;</c>, on*-handlers, and javascript: URLs.
///
/// Block-structure HTML (the nested tables that lay the signature out) is
/// generated server-side by <see cref="SignatureRenderer"/> and never passes
/// through here — only the user-authored text inside a block does.
public sealed class SignatureHtmlSanitizer : ISignatureHtmlSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    public SignatureHtmlSanitizer()
    {
        _sanitizer = new HtmlSanitizer();
        Configure(_sanitizer);
    }

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return _sanitizer.Sanitize(html);
    }

    private static void Configure(HtmlSanitizer s)
    {
        s.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "span", "div",
            "strong", "b", "em", "i", "u", "s", "small", "sub", "sup",
            "a", "ul", "ol", "li",
            "h1", "h2", "h3", "h4", "h5", "h6",
        })
        {
            s.AllowedTags.Add(tag);
        }

        s.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "title", "style", "class" })
        {
            s.AllowedAttributes.Add(attr);
        }

        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");
        s.AllowedSchemes.Add("tel");

        // Curated inline-CSS allow-list. No background-image / url() — text
        // blocks must not pull external resources.
        s.AllowedCssProperties.Clear();
        foreach (var prop in new[]
        {
            "color", "background-color",
            "font-size", "font-family", "font-weight", "font-style",
            "text-align", "text-decoration", "text-transform",
            "line-height", "letter-spacing",
            "padding", "padding-top", "padding-bottom", "padding-left", "padding-right",
            "margin", "margin-top", "margin-bottom", "margin-left", "margin-right",
        })
        {
            s.AllowedCssProperties.Add(prop);
        }

        s.AllowedAtRules.Clear();
        s.AllowDataAttributes = false;
    }
}

public interface ISignatureHtmlSanitizer
{
    string Sanitize(string? html);
}
