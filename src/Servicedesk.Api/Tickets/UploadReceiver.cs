using Microsoft.AspNetCore.Http.Features;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Tickets;

/// The single multipart-upload intake for every user-facing upload route
/// (ticket + portal, knowledge base, feedback, compose-template images):
/// request-size limit, size cap, server-side MIME sniff, HTML refusal,
/// streamed blob write, filename sanitising. Authorization and the row
/// insert stay with the caller — this only receives and stores the bytes.
internal static class UploadReceiver
{
    internal readonly record struct Received(string Filename, string MimeType, BlobWriteResult Write);

    /// Either an error response to return verbatim, or the stored file.
    internal readonly record struct Outcome(IResult? Error, Received? Ok);

    private const long DefaultMaxBytes = 26_214_400; // 25 MB safety net

    // Multipart framing (boundaries, part headers, other fields) on top of
    // the file itself.
    private const long MultipartOverheadBytes = 1_048_576;

    internal static async Task<Outcome> ReceiveAsync(
        HttpContext http, IBlobStore blobs, ISettingsService settings, CancellationToken ct,
        bool imagesOnly = false,
        string maxBytesSettingKey = SettingKeys.Storage.MaxAttachmentBytes)
    {
        if (!http.Request.HasFormContentType)
            return Fail(Results.BadRequest(new { error = "multipart/form-data required." }));

        var maxBytes = await settings.GetAsync<long>(maxBytesSettingKey, ct);
        if (maxBytes <= 0) maxBytes = DefaultMaxBytes;
        var bodyLimit = maxBytes + MultipartOverheadBytes;

        // Kestrel's own limit follows the setting for this request (its global
        // default, ~28.6 MB, stays in force for every other route). nginx
        // client_max_body_size is the outer bound in front of both.
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = bodyLimit;

        // Cheap pre-flight on an honest Content-Length before anything is read.
        if (http.Request.ContentLength is long advertised && advertised > bodyLimit)
            return Fail(TooLarge(maxBytes, maxBytesSettingKey));

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Fail(TooLarge(maxBytes, maxBytesSettingKey));
        }
        catch (InvalidDataException)
        {
            // Form-reader limits (e.g. multipart body length).
            return Fail(TooLarge(maxBytes, maxBytesSettingKey));
        }

        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Fail(Results.BadRequest(new { error = "Upload a non-empty file in the 'file' field." }));
        if (file.Length > maxBytes)
            return Fail(TooLarge(maxBytes, maxBytesSettingKey));

        // HTML is refused outright: an HTML "attachment" served back is XSS
        // bait even with the inline guard. Image-only routes refuse anything
        // that does not sniff as a raster image (SVG never sniffs as image/*).
        SniffedBlobWrite stored;
        await using (var source = file.OpenReadStream())
        {
            stored = await SniffedBlobWriter.WriteAsync(blobs, source, file.ContentType, file.FileName,
                mime => !string.Equals(mime, "text/html", StringComparison.OrdinalIgnoreCase)
                        && (!imagesOnly || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)),
                ct);
        }
        if (stored.Write is not { } write)
        {
            return Fail(string.Equals(stored.MimeType, "text/html", StringComparison.OrdinalIgnoreCase)
                ? Results.BadRequest(new { error = "HTML uploads are not allowed." })
                : Results.BadRequest(new { error = "Only image files are allowed." }));
        }

        // The written size is authoritative (file.Length comes from the form
        // parser). An over-size blob stays unreferenced; the blob GC reclaims it.
        if (write.SizeBytes > maxBytes)
            return Fail(TooLarge(maxBytes, maxBytesSettingKey));

        return new Outcome(null, new Received(SanitizeFilename(file.FileName), stored.MimeType, write));
    }

    internal static string SanitizeFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "attachment";
        // Strip directory components (some browsers send full paths) and
        // refuse anything that the OS would treat specially. Truncate so an
        // adversarial 4 KB filename can't blow the table column.
        var leaf = Path.GetFileName(input);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = leaf.Select(c => invalid.Contains(c) || c == '<' || c == '>' || c == ':' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimStart('.');
        if (string.IsNullOrEmpty(s)) s = "attachment";
        return s.Length > 200 ? s[..200] : s;
    }

    private static Outcome Fail(IResult error) => new(error, null);

    private static IResult TooLarge(long maxBytes, string settingKey) =>
        Results.Json(new
        {
            error = $"File exceeds the {FormatLimit(maxBytes)} limit ({settingKey}).",
        }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static string FormatLimit(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
