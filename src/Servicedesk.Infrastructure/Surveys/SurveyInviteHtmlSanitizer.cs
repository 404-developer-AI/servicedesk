using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Servicedesk.Infrastructure.Surveys;

/// HTML sanitizer for the survey invitation-email body. Same conservative
/// allow-list as the KB sanitizer (paragraphs, headings, lists, links, basic
/// inline marks), but with one survey-specific exception: a placeholder URL
/// of the form <c>{{token}}</c> is preserved in <c>href</c>/<c>src</c>.
///
/// Why this exists: the invite body is admin-authored HTML that links the
/// survey via <c>&lt;a href="{{survey.link}}"&gt;</c>. The stock sanitizer
/// validates URLs against an allow-list of schemes and strips anything that
/// is not a real URL — which silently removed the token href and broke the
/// survey link. Tokens are replaced with the real, server-built URL at
/// dispatch time (see <see cref="SurveyDispatchService"/>), so letting the
/// literal <c>{{...}}</c> survive here is safe: the value an attacker could
/// reach is never the final URL, and a token that never resolves is inert
/// text, not a live link.
public sealed class SurveyInviteHtmlSanitizer : ISurveyInviteHtmlSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    // A pure `{{token}}` placeholder — ASCII identifier + dots only, optional
    // surrounding whitespace tolerated the same way the runtime resolver does.
    private static readonly Regex TokenUrl = new(
        @"^\s*\{\{[\w.]+\}\}\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SurveyInviteHtmlSanitizer()
    {
        _sanitizer = new HtmlSanitizer();
        ConfigureAllowList(_sanitizer);

        // Re-admit token-only URLs the base sanitizer would otherwise drop.
        // The event fires after the stock URL validation has already nulled
        // out the sanitized value for a non-URL like `{{survey.link}}`; we
        // put the original token back so dispatch-time substitution can run.
        _sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl is not null && TokenUrl.IsMatch(e.OriginalUrl))
                e.SanitizedUrl = e.OriginalUrl;
        };
    }

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return _sanitizer.Sanitize(html);
    }

    private static void ConfigureAllowList(HtmlSanitizer sanitizer)
    {
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "hr",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "strong", "em", "u", "s", "code", "pre", "blockquote",
            "ul", "ol", "li",
            "a",
            "span", "div",
        })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "title", "class" })
        {
            sanitizer.AllowedAttributes.Add(attr);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("tel");

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowDataAttributes = false;
    }
}

public interface ISurveyInviteHtmlSanitizer
{
    string Sanitize(string? html);
}
