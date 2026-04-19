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
}
