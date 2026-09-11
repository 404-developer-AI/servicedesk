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

    /// Strong ETag for a blob-backed response. The blob store is
    /// content-addressed, so the hash alone pins the *bytes* — but an HTTP
    /// validator has to cover the whole representation, and the served
    /// Content-Type is part of it. v0.1.7 rewrote the MIME type of misfiled
    /// PDFs without touching the bytes; a browser that had cached the old
    /// text/plain response kept rendering the file as text: fresh for the
    /// full max-age, then revalidated forever with 304 because the hash still
    /// matched, and a 304 never replaces the cached Content-Type. Folding the
    /// MIME type in turns a reclassification into a new validator. Every
    /// endpoint that serves a blob must build its ETag here.
    internal static string ETag(string? contentHash, string? mimeType)
    {
        var type = string.IsNullOrWhiteSpace(mimeType)
            ? "application/octet-stream"
            : mimeType.Trim().ToLowerInvariant();
        // entity-tag characters are %x21 / %x23-7E (RFC 9110 §8.8.3): never a
        // double quote, whitespace or a control character — a sender-declared
        // MIME type is untrusted input, so anything else is squashed.
        Span<char> safe = stackalloc char[type.Length];
        for (var i = 0; i < type.Length; i++)
        {
            var c = type[i];
            safe[i] = c == '!' || (c >= '#' && c <= '~') ? c : '_';
        }
        return $"\"{contentHash ?? string.Empty}:{new string(safe)}\"";
    }

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
