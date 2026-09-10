using System.Text;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

public class MimeSnifferTests
{
    [Fact]
    public void Detects_png_from_magic_bytes_even_with_lying_client_mime()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFF };
        var sniffed = MimeSniffer.Sniff(bytes, clientMime: "text/plain", filename: "fake.txt");
        Assert.Equal("image/png", sniffed);
    }

    [Fact]
    public void Detects_jpeg_from_magic_bytes()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        Assert.Equal("image/jpeg", MimeSniffer.Sniff(bytes, null, "x.jpg"));
    }

    [Fact]
    public void Detects_pdf_from_magic_bytes()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n....");
        Assert.Equal("application/pdf", MimeSniffer.Sniff(bytes, "application/octet-stream", "doc.pdf"));
    }

    [Fact]
    public void Flags_html_disguised_as_other_type_so_caller_can_refuse()
    {
        var bytes = Encoding.ASCII.GetBytes("<!DOCTYPE html>\n<html><body><script>alert(1)</script></body></html>");
        Assert.Equal("text/html", MimeSniffer.Sniff(bytes, clientMime: "image/png", filename: "evil.png"));
    }

    [Fact]
    public void Office_zip_recognised_as_docx()
    {
        // PK\x03\x04 + a synthetic snippet that mimics the [Content_Types].xml head of a docx.
        var head = Encoding.ASCII.GetBytes("PK\x03\x04..[Content_Types].xml.....word/document.xml");
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            MimeSniffer.Sniff(head, "application/octet-stream", "report.docx"));
    }

    [Fact]
    public void Plain_text_falls_back_to_text_plain_via_heuristic()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world this is a log file with some lines\nline two");
        Assert.Equal("text/plain", MimeSniffer.Sniff(bytes, clientMime: null, filename: "app.log"));
    }

    [Fact]
    public void Unknown_binary_lands_on_octet_stream()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0xFF };
        Assert.Equal("application/octet-stream", MimeSniffer.Sniff(bytes, null, "blob.bin"));
    }

    [Fact]
    public void Trusted_client_mime_passes_through_when_magic_is_silent()
    {
        var bytes = Encoding.UTF8.GetBytes("col1,col2\n1,2\n3,4");
        Assert.Equal("text/csv", MimeSniffer.Sniff(bytes, clientMime: "text/csv", filename: "data.csv"));
    }

    [Fact]
    public void Untrusted_client_mime_is_ignored_when_content_says_otherwise()
    {
        // Adversary claims 'image/png' but the bytes are plain text — sniffer
        // refuses to honour the lie and falls back to the heuristic.
        var bytes = Encoding.UTF8.GetBytes("totally not a png, just text content here");
        var sniffed = MimeSniffer.Sniff(bytes, clientMime: "image/png", filename: "x.png");
        Assert.NotEqual("image/png", sniffed);
        Assert.Equal("text/plain", sniffed);
    }

    // ------------------------------------------------------------------
    // v0.1.7 — whitespace-prefixed PDFs. bpost's notifier prepends a CRLF to
    // every attachment body; before the fix a PDF whose first 512 bytes were
    // ASCII object headers sniffed as text/plain (the timeline preview then
    // showed raw PDF source) and one whose compressed stream started early
    // sniffed as octet-stream.
    // ------------------------------------------------------------------

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return all.ToArray();
    }

    // Mirrors the misfiled production file: %PDF-1.4, a few binary comment
    // bytes, then a long run of ASCII object dictionaries (no NUL in 512).
    private static byte[] AsciiHeavyPdf(string prefix = "")
        => Concat(
            Latin1(prefix + "%PDF-1.4\n%"),
            new byte[] { 0xF6, 0xE4, 0xFC, 0xDF },
            Latin1("\n1 0 obj\n<<\n/Type /Catalog\n/Version /1.7\n/Pages 2 0 R\n>>\nendobj\n" +
                   new string('x', 600)));

    // Mirrors the second file: compressed stream (with NULs) inside the
    // first 512 bytes.
    private static byte[] BinaryHeavyPdf(string prefix = "")
        => Concat(
            Latin1(prefix + "%PDF-1.5\n%"),
            new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 },
            Latin1("\n2 0 obj\n<</Filter/FlateDecode/Length 300>>stream\n"),
            Enumerable.Range(0, 400).Select(i => (byte)(i % 7 == 0 ? 0 : 0x80 + (i % 100))).ToArray());

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("  \t\r\n")]
    public void Pdf_signature_is_recognised_with_or_without_leading_whitespace(string prefix)
    {
        Assert.Equal("application/pdf", MimeSniffer.Sniff(AsciiHeavyPdf(prefix), "application/pdf", "proof.pdf"));
        Assert.Equal("application/pdf", MimeSniffer.Sniff(BinaryHeavyPdf(prefix), "application/pdf", "label.pdf"));
    }

    [Fact]
    public void Whitespace_prefixed_pdf_no_longer_depends_on_the_declared_type()
    {
        // The inbound worker passes the sender's label; the reclassify
        // service passes the old (wrong) verdict. Both must land on PDF.
        Assert.Equal("application/pdf", MimeSniffer.Sniff(AsciiHeavyPdf("\r\n"), "text/plain", "proof.pdf"));
        Assert.Equal("application/pdf", MimeSniffer.Sniff(BinaryHeavyPdf("\r\n"), "application/octet-stream", "label.pdf"));
        Assert.Equal("application/pdf", MimeSniffer.Sniff(AsciiHeavyPdf("\r\n"), null, null));
    }

    [Fact]
    public void A_pdf_named_file_without_signature_is_not_promoted_to_pdf()
    {
        // The filename is never enough: a text file called .pdf stays text,
        // and a declared application/pdf without magic is not trusted.
        var text = Latin1("This is not a PDF at all.\n" + new string('y', 300));
        Assert.Equal("text/plain", MimeSniffer.Sniff(text, "application/pdf", "fake.pdf"));

        var binary = Concat(Latin1("junk"), new byte[] { 0, 1, 2, 3, 0, 0, 0xFF }, Latin1(new string('z', 100)));
        Assert.Equal("application/octet-stream", MimeSniffer.Sniff(binary, "application/pdf", "fake.pdf"));
    }

    [Fact]
    public void Html_disguised_with_leading_whitespace_is_still_flagged()
    {
        Assert.Equal("text/html", MimeSniffer.Sniff(Latin1("\r\n<html><body>x</body></html>"), "application/pdf", "x.pdf"));
    }
}
