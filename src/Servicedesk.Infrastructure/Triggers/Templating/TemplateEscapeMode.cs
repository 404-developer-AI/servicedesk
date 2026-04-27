namespace Servicedesk.Infrastructure.Triggers.Templating;

/// Per-call escaping policy for <see cref="ITriggerTemplateRenderer"/>.
/// Picked by the caller based on the field being rendered:
/// <see cref="Html"/> for <c>body_html</c> so a customer-supplied name
/// containing &lt;script&gt; never lands inline as live markup;
/// <see cref="PlainText"/> for subject lines and headers so a value
/// containing CR/LF cannot inject a sibling header (e.g. a smuggled Bcc).
public enum TemplateEscapeMode
{
    Html,
    PlainText,
}
