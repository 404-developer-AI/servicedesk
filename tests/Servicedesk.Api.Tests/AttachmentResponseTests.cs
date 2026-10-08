using Servicedesk.Api.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the blob ETag contract: the validator must change whenever the served
/// representation changes, and the MIME type is part of that representation
/// even when the bytes are not (the v0.1.7 PDF reclassification left the
/// content hash untouched, so a hash-only ETag kept stale text/plain responses
/// alive in browser caches indefinitely).
public class AttachmentResponseTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Same_bytes_with_a_different_mime_type_get_a_different_etag()
    {
        var before = AttachmentResponse.ETag(Hash, "text/plain");
        var after = AttachmentResponse.ETag(Hash, "application/pdf");
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Etag_is_stable_for_identical_inputs()
    {
        Assert.Equal(
            AttachmentResponse.ETag(Hash, "application/pdf"),
            AttachmentResponse.ETag(Hash, "application/pdf"));
    }

    [Fact]
    public void Etag_is_a_quoted_strong_validator_containing_the_hash()
    {
        var etag = AttachmentResponse.ETag(Hash, "image/png");
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        Assert.Contains(Hash, etag);
        Assert.DoesNotContain("W/", etag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_mime_type_falls_back_to_octet_stream(string? mime)
    {
        Assert.Equal(
            AttachmentResponse.ETag(Hash, "application/octet-stream"),
            AttachmentResponse.ETag(Hash, mime));
    }

    [Fact]
    public void Mime_type_casing_and_surrounding_whitespace_do_not_change_the_etag()
    {
        Assert.Equal(
            AttachmentResponse.ETag(Hash, "application/pdf"),
            AttachmentResponse.ETag(Hash, "  Application/PDF "));
    }

    [Fact]
    public void Sender_declared_mime_type_cannot_break_the_entity_tag_grammar()
    {
        // A mail sender controls this string; quotes, spaces and control
        // characters would corrupt the header or split the validator.
        var etag = AttachmentResponse.ETag(Hash, "text/plain; charset=\"utf-8\"\r\nX-Injected: 1");
        var inner = etag[1..^1];
        Assert.DoesNotContain('"', inner);
        Assert.All(inner, c => Assert.True(c == '!' || (c >= '#' && c <= '~'), $"illegal char 0x{(int)c:X2}"));
    }

    // Inline timeline images skip the audit row; everything else must still log,
    // because `inline` is a client-controlled query flag.
    [Theory]
    [InlineData(true, "image/png", true)]
    [InlineData(true, "image/jpeg", true)]
    [InlineData(true, "IMAGE/GIF", true)]
    [InlineData(false, "image/png", false)]        // explicit download/open
    [InlineData(true, "application/pdf", false)]   // document: always audited
    [InlineData(true, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData(true, "image/svg+xml", false)]     // not inline-safe
    [InlineData(true, "text/html", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    public void Only_inline_raster_images_skip_the_audit_row(bool inline, string? mime, bool unaudited)
    {
        Assert.Equal(unaudited, AttachmentResponse.IsUnauditedInlineImage(inline, mime));
    }
}
