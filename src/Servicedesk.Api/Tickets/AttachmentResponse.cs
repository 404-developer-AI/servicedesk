namespace Servicedesk.Api.Tickets;

/// The single place that decides how an attachment body is served (audit
/// v0.1.1 #2). `?inline=true` is honoured only for content types that cannot
/// become a same-origin document or script: an inbound-mail attachment keeps
/// the *sender's* declared MIME type, and a `text/html` body rendered inline
/// on the app origin is a full CSP bypass (`script-src 'self'` would then
/// load a sibling attachment as a script with the victim's session). HTML,
/// SVG and XML therefore always force a download. Every attachment route —
/// agent, mail, public KB and the customer portal — must serve through this
/// helper so a future route cannot forget the guard.
internal static class AttachmentResponse
{
    /// True when the content type is safe to render inline: nothing that a
    /// browser would treat as a scriptable same-origin document.
    internal static bool IsInlineSafe(string contentType) =>
        !contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
        && !contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)
        && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        && !contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        && !contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase);

    /// Serves the blob stream, downgrading a requested inline render to a
    /// plain download whenever the type is not inline-safe.
    internal static IResult File(Stream stream, string? mimeType, string? originalFilename, bool inlineRequested)
    {
        var fileName = string.IsNullOrWhiteSpace(originalFilename) ? "attachment" : originalFilename;
        var contentType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
        var inline = inlineRequested && IsInlineSafe(contentType);
        return inline
            ? Results.File(stream, contentType, fileDownloadName: null, enableRangeProcessing: true)
            : Results.File(stream, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
    }
}
