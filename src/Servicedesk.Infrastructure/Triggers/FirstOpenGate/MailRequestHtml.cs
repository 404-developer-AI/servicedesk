using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Triggers.FirstOpenGate;

/// Shapes an inbound-mail HTML body for the first-open gate's
/// original-request panel. Display hygiene only — XSS safety is handled by
/// the frontend's DOMPurify pass on render, same as the timeline.
public static class MailRequestHtml
{
    /// Removes every img tag. Inline mail images reference cid: URLs that
    /// can't resolve inside the gate dialog (the cid-rewrite enricher only
    /// runs on the timeline), and remote images would leak the agent's
    /// presence to external trackers — the agent downloads the .eml when
    /// the pictures matter. Also drops picture/source wrappers so a
    /// stripped img doesn't leave a broken responsive-image shell behind.
    public static string StripImages(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var result = PictureBlock.Replace(html, string.Empty);
        result = SourceTag.Replace(result, string.Empty);
        return ImgTag.Replace(result, string.Empty);
    }

    private static readonly Regex ImgTag = new(
        @"<img\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SourceTag = new(
        @"<source\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PictureBlock = new(
        @"</?picture\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
