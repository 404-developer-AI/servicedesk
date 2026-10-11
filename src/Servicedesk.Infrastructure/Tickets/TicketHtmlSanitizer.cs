using Ganss.Xss;

namespace Servicedesk.Infrastructure.Tickets;

/// Server-side pass over agent-supplied ticket HTML (notes, calls, new-ticket
/// bodies, event edits, outbound mail bodies) at the API boundary. The
/// timeline already renders through DOMPurify; this is defence in depth so
/// nothing scriptable is ever persisted, and it keeps inline <c>data:</c>
/// payloads out of <c>ticket_events</c> (images are uploaded and referenced
/// by URL — the editor never embeds base64).
///
/// Deliberately broader than the KB sanitizer: the composer's custom nodes
/// carry state in <c>data-*</c> attributes (mentions, <c>::</c> order pills,
/// intake-form chips, the signature marker) and order detection reads them
/// back from the stored HTML, so data attributes and classes must survive.
/// Server-generated HTML (inbound mail, signatures, gate notes) does not pass
/// through here.
public sealed class TicketHtmlSanitizer : ITicketHtmlSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    public TicketHtmlSanitizer()
    {
        // Ganss defaults: formatting tags, tables, lists, links, images,
        // filtered inline styles; scripts, event handlers, forms, iframes and
        // javascript:/vbscript: URLs are dropped.
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowDataAttributes = true;
        _sanitizer.AllowedAttributes.Add("class");
        _sanitizer.AllowedAttributes.Add("target");
        _sanitizer.AllowedAttributes.Add("rel");

        // No data: (inline payloads) — relative URLs (/api/...) stay allowed.
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");
        _sanitizer.AllowedSchemes.Add("tel");
    }

    public string? Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? html : _sanitizer.Sanitize(html);
}

public interface ITicketHtmlSanitizer
{
    /// Returns null/whitespace input unchanged.
    string? Sanitize(string? html);
}
