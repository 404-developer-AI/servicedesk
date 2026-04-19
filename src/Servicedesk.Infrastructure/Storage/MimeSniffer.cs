using System.Text;

namespace Servicedesk.Infrastructure.Storage;

/// Server-side content-type sniffing for user-uploaded blobs. Inspects the
/// first <see cref="SniffWindowBytes"/> of a stream and returns the best-guess
/// MIME type based on magic bytes. Used to override (or fall back from) the
/// browser-supplied Content-Type so an attacker cannot upload e.g. an HTML
/// file labelled <c>image/png</c> and have a victim's browser execute it
/// when fetched via <c>?inline=true</c>.
///
/// Built-in heuristic — intentionally no third-party dependency. Covers the
/// common file types a helpdesk sees (images, PDF, ZIP/Office, plain text).
/// Unknown content falls back to <c>application/octet-stream</c>.
public static class MimeSniffer
{
    public const int SniffWindowBytes = 512;

    /// MIME types that are always allowed to round-trip the user-supplied
    /// label (mostly because the magic-byte check is unreliable for them or
    /// they share a generic header — e.g. legacy DOC vs XLS both start with
    /// the OLE2 signature). Listed once here so the upload endpoint and the
    /// download endpoint share the same trust boundary.
    private static readonly HashSet<string> AllowedFallbackTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream",
        "text/plain",
        "text/csv",
        "application/json",
        "application/xml",
        "text/xml",
    };

    public static string Sniff(ReadOnlySpan<byte> bytes, string? clientMime, string? filename)
    {
        var detected = DetectFromMagic(bytes);
        if (detected is not null) return detected;

        // Magic bytes were ambiguous. Trust an explicit client MIME only if
        // it's a low-risk, well-known type — never `text/html` or anything
        // image-like (those must be detectable, otherwise it's an attack).
        if (!string.IsNullOrWhiteSpace(clientMime))
        {
            var trimmed = clientMime.Trim().ToLowerInvariant();
            if (AllowedFallbackTypes.Contains(trimmed) ||
                trimmed.StartsWith("application/vnd.openxmlformats-officedocument.", StringComparison.Ordinal) ||
                trimmed.StartsWith("application/vnd.ms-", StringComparison.Ordinal) ||
                trimmed.StartsWith("application/vnd.oasis.opendocument.", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        // Last-ditch heuristic: text vs binary. Lets a `.log`/`.txt` file
        // through as text/plain instead of forcing a download as octet-stream.
        if (LooksLikeText(bytes)) return "text/plain";

        // Filename hint as a tiebreaker for known plaintext-ish extensions.
        var ext = Path.GetExtension(filename ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "txt" or "log" or "md" => "text/plain",
            "csv" => "text/csv",
            "json" => "application/json",
            "xml" => "application/xml",
            _ => "application/octet-stream",
        };
    }

    /// Magic-byte signatures. Order matters: more-specific signatures (like
    /// the Office Open XML zipped containers) must be checked before their
    /// generic parent (a plain ZIP). Returns <c>null</c> when no signature
    /// matches and the caller should fall back to the heuristic.
    private static string? DetectFromMagic(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return null;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";

        // JPEG: FF D8 FF
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";

        // GIF87a / GIF89a
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38
            && (b[4] == 0x37 || b[4] == 0x39) && b[5] == 0x61) return "image/gif";

        // WebP: RIFF .... WEBP
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";

        // BMP: 42 4D
        if (b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";

        // SVG (XML-prefixed) → handled in text-heuristic below; we never
        // sniff SVG to image/* because of script-execution risk on inline
        // viewing. SVGs always come back as text/xml.

        // PDF: %PDF
        if (b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return "application/pdf";

        // ZIP / Office Open XML / ODF — start with PK\x03\x04
        if (b[0] == 0x50 && b[1] == 0x4B && (b[2] == 0x03 || b[2] == 0x05) && (b[3] == 0x04 || b[3] == 0x06))
        {
            // Look for "[Content_Types].xml" or "word/" / "xl/" / "ppt/" markers
            // in the first 512 bytes — if absent the file is a plain zip.
            var window = Encoding.ASCII.GetString(b);
            if (window.Contains("word/", StringComparison.Ordinal))
                return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            if (window.Contains("xl/", StringComparison.Ordinal))
                return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            if (window.Contains("ppt/", StringComparison.Ordinal))
                return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            if (window.Contains("mimetypeapplication/vnd.oasis.opendocument.text", StringComparison.Ordinal))
                return "application/vnd.oasis.opendocument.text";
            if (window.Contains("mimetypeapplication/vnd.oasis.opendocument.spreadsheet", StringComparison.Ordinal))
                return "application/vnd.oasis.opendocument.spreadsheet";
            return "application/zip";
        }

        // Legacy OLE2 Compound File (.doc, .xls, .ppt, .msg): D0 CF 11 E0 A1 B1 1A E1
        if (b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0
            && b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1)
            return "application/x-ole-storage";

        // RAR / 7z / GZIP / TAR — return as octet-stream (rare in helpdesk)
        if (b[0] == 0x52 && b[1] == 0x61 && b[2] == 0x72 && b[3] == 0x21) return "application/vnd.rar";
        if (b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF) return "application/x-7z-compressed";
        if (b[0] == 0x1F && b[1] == 0x8B) return "application/gzip";

        // Reject HTML disguised as something else: <!DOCTYPE / <html / <body
        // — the upload endpoint surfaces this as `text/html` so the call site
        // can refuse it. Browsers will *always* render this as HTML if served
        // inline with the wrong type, so we must not let it through silently.
        if (StartsWithIgnoringWhitespaceAndCase(b, "<!doctype html") ||
            StartsWithIgnoringWhitespaceAndCase(b, "<html") ||
            StartsWithIgnoringWhitespaceAndCase(b, "<head") ||
            StartsWithIgnoringWhitespaceAndCase(b, "<body") ||
            StartsWithIgnoringWhitespaceAndCase(b, "<script"))
        {
            return "text/html";
        }

        return null;
    }

    private static bool StartsWithIgnoringWhitespaceAndCase(ReadOnlySpan<byte> bytes, string needle)
    {
        var i = 0;
        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n')) i++;
        if (i + needle.Length > bytes.Length) return false;
        for (var j = 0; j < needle.Length; j++)
        {
            var c = (char)bytes[i + j];
            if (char.ToLowerInvariant(c) != needle[j]) return false;
        }
        return true;
    }

    /// Heuristic: a window with no NUL bytes and a high proportion of
    /// printable / common-whitespace ASCII is probably text. Conservative;
    /// false-negatives become application/octet-stream which is harmless.
    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return false;
        var printable = 0;
        foreach (var b in bytes)
        {
            if (b == 0) return false; // any NUL → binary
            if (b == 0x09 || b == 0x0A || b == 0x0D) { printable++; continue; }
            if (b >= 0x20 && b <= 0x7E) { printable++; continue; }
            if (b >= 0x80) printable++; // assume valid UTF-8 high bytes
        }
        return printable * 100 / bytes.Length >= 95;
    }
}
