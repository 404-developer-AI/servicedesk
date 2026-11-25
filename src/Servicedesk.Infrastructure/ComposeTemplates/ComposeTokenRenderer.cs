using System.Net;
using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.ComposeTemplates;

/// How a resolved token value is written into the template.
public enum ComposeTokenEscape
{
    /// The template is admin-authored HTML; values are user-controlled
    /// strings and are HTML-encoded so they can never inject markup.
    Html,

    /// The template is a plain-text field (a mail subject): values go in
    /// raw, with CR/LF collapsed so a value can never inject a header line.
    PlainText,
}

/// Result of a server-side render: the output plus every `{{token}}` that
/// stayed in it (unknown token, or a known token without a value).
public sealed record ComposeTokenRenderResult(string Output, IReadOnlyList<string> MissingTokens);

/// v0.1.67 — the one server-side `{{token}}` substitution. Mirrors the
/// client's `substituteComposeTokens` (lib/composeTokens.ts): tolerant of
/// the whitespace variants Tiptap introduces (`{{ contact.firstName }}`),
/// an empty value leaves the raw placeholder in place and reports it as
/// missing so a caller can refuse to send a mail with a visible gap.
/// Used by campaigns and the survey invite.
public static partial class ComposeTokenRenderer
{
    [GeneratedRegex(@"\{\{\s*([\w.]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public static ComposeTokenRenderResult Render(
        string? template, IReadOnlyDictionary<string, string> tokens, ComposeTokenEscape escape)
    {
        if (string.IsNullOrEmpty(template))
            return new ComposeTokenRenderResult(string.Empty, Array.Empty<string>());

        var missing = new List<string>();
        var output = TokenRegex().Replace(template, match =>
        {
            var canonical = "{{" + match.Groups[1].Value + "}}";
            if (!tokens.TryGetValue(canonical, out var value) || string.IsNullOrEmpty(value))
            {
                if (!missing.Contains(canonical, StringComparer.Ordinal)) missing.Add(canonical);
                return match.Value;
            }
            return escape == ComposeTokenEscape.Html
                ? WebUtility.HtmlEncode(value)
                : CollapseLineBreaks(value);
        });

        if (escape == ComposeTokenEscape.PlainText) output = CollapseLineBreaks(output);
        return new ComposeTokenRenderResult(output, missing);
    }

    private static string CollapseLineBreaks(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
             .Replace('\r', ' ')
             .Replace('\n', ' ');
}
