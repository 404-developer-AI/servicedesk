using Servicedesk.Infrastructure.KnowledgeBase;
using Xunit;

namespace Servicedesk.Api.Tests;

public class KbHtmlSanitizerTests
{
    private static readonly KbHtmlSanitizer Sanitizer = new();

    [Fact]
    public void Empty_input_returns_empty_string()
    {
        Assert.Equal(string.Empty, Sanitizer.Sanitize(null));
        Assert.Equal(string.Empty, Sanitizer.Sanitize(""));
        Assert.Equal(string.Empty, Sanitizer.Sanitize("   "));
    }

    [Fact]
    public void Strips_script_tags()
    {
        var output = Sanitizer.Sanitize("<p>safe</p><script>alert('xss')</script>");
        Assert.Contains("safe", output);
        Assert.DoesNotContain("<script", output);
        Assert.DoesNotContain("alert", output);
    }

    [Fact]
    public void Strips_on_event_handlers()
    {
        var output = Sanitizer.Sanitize("<a href=\"https://example.com\" onclick=\"alert(1)\">link</a>");
        Assert.Contains("href=\"https://example.com\"", output);
        Assert.DoesNotContain("onclick", output);
    }

    [Fact]
    public void Strips_javascript_urls()
    {
        var output = Sanitizer.Sanitize("<a href=\"javascript:alert(1)\">click</a>");
        Assert.DoesNotContain("javascript:", output);
    }

    [Fact]
    public void Allows_https_http_mailto_tel_schemes()
    {
        Assert.Contains("https://", Sanitizer.Sanitize("<a href=\"https://example.com\">x</a>"));
        Assert.Contains("http://", Sanitizer.Sanitize("<a href=\"http://example.com\">x</a>"));
        Assert.Contains("mailto:", Sanitizer.Sanitize("<a href=\"mailto:foo@bar.com\">x</a>"));
        Assert.Contains("tel:", Sanitizer.Sanitize("<a href=\"tel:+32123456789\">x</a>"));
    }

    [Fact]
    public void Allows_basic_rich_text_marks()
    {
        var input = "<p><strong>bold</strong> <em>italic</em> <code>x</code> <u>u</u> <s>s</s></p>";
        var output = Sanitizer.Sanitize(input);
        Assert.Contains("<strong>", output);
        Assert.Contains("<em>", output);
        Assert.Contains("<code>", output);
    }

    [Fact]
    public void Allows_headings_lists_and_blockquote()
    {
        var input = "<h1>title</h1><ul><li>a</li></ul><ol><li>b</li></ol><blockquote>q</blockquote>";
        var output = Sanitizer.Sanitize(input);
        Assert.Contains("<h1>", output);
        Assert.Contains("<ul>", output);
        Assert.Contains("<ol>", output);
        Assert.Contains("<blockquote>", output);
    }

    [Fact]
    public void Allows_tables()
    {
        var input = "<table><thead><tr><th>h</th></tr></thead><tbody><tr><td>v</td></tr></tbody></table>";
        var output = Sanitizer.Sanitize(input);
        Assert.Contains("<table>", output);
        Assert.Contains("<thead>", output);
        Assert.Contains("<th>", output);
        Assert.Contains("<td>", output);
    }

    [Fact]
    public void Strips_iframe_and_object_tags()
    {
        var output = Sanitizer.Sanitize("<iframe src=\"https://evil\"></iframe><object data=\"x.swf\"></object>");
        Assert.DoesNotContain("<iframe", output);
        Assert.DoesNotContain("<object", output);
    }

    [Fact]
    public void Strips_data_attributes()
    {
        var output = Sanitizer.Sanitize("<p data-foo=\"x\" data-bar=\"y\">text</p>");
        Assert.DoesNotContain("data-foo", output);
        Assert.DoesNotContain("data-bar", output);
    }

    [Fact]
    public void Keeps_relative_api_image_sources()
    {
        // KB, feedback, and mail-template bodies (v0.0.92) all embed inline
        // images as relative URLs to their authenticated download endpoints.
        // Those must survive the persist-time sanitize or every saved body
        // silently loses its images.
        var input = "<img src=\"/api/compose-templates/images/11111111-2222-3333-4444-555555555555\" alt=\"logo\">";
        var output = Sanitizer.Sanitize(input);
        Assert.Contains("/api/compose-templates/images/11111111-2222-3333-4444-555555555555", output);
        Assert.Contains("alt=\"logo\"", output);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("tip")]
    [InlineData("warning")]
    [InlineData("important")]
    public void Keeps_article_callouts(string type)
    {
        var output = Sanitizer.Sanitize(
            $"<div class=\"sd-callout sd-callout--{type}\"><p>Let op</p></div>");
        Assert.Contains($"class=\"sd-callout sd-callout--{type}\"", output);
        Assert.Contains("<p>Let op</p>", output);
    }

    [Fact]
    public void Keeps_interface_labels_and_image_sizes()
    {
        var output = Sanitizer.Sanitize(
            "<p>Klik op <span class=\"sd-ui\">Add note</span></p>" +
            "<img src=\"/api/kb/articles/a/attachments/b\" alt=\"x\" class=\"max-w-full h-auto rounded-md sd-img--md\">");
        Assert.Contains("<span class=\"sd-ui\">Add note</span>", output);
        Assert.Contains("class=\"sd-img--md\"", output);
        // The editor's own utility classes on <img> are not content classes.
        Assert.DoesNotContain("max-w-full", output);
    }

    [Fact]
    public void Strips_classes_outside_the_content_allow_list()
    {
        // Utility or app-chrome classes would let an author restyle or
        // overlay the surrounding UI from inside a body.
        var output = Sanitizer.Sanitize(
            "<div class=\"fixed inset-0 z-50 sd-sidebar\"><p class=\"glass-card sd-callout\">x</p></div>");
        Assert.DoesNotContain("fixed", output);
        Assert.DoesNotContain("inset-0", output);
        Assert.DoesNotContain("z-50", output);
        Assert.DoesNotContain("sd-sidebar", output);
        Assert.DoesNotContain("glass-card", output);
        Assert.Contains("sd-callout", output);
    }

    [Fact]
    public void Keeps_editor_tables_and_drops_inline_widths()
    {
        // Shape Tiptap's table extension serialises: inline min-width styles
        // and a colgroup, cells wrapping paragraphs.
        var input =
            "<table style=\"min-width: 75px;\"><colgroup><col style=\"min-width: 25px;\"></colgroup><tbody>" +
            "<tr><th colspan=\"1\" rowspan=\"1\"><p>Stap</p></th><th colspan=\"1\" rowspan=\"1\"><p>Limiet</p></th></tr>" +
            "<tr><td colspan=\"1\" rowspan=\"1\"><p>Call-backs</p></td><td colspan=\"1\" rowspan=\"1\"><p>15 min</p></td></tr>" +
            "</tbody></table>";
        var output = Sanitizer.Sanitize(input);
        Assert.Contains("<th colspan=\"1\" rowspan=\"1\"><p>Stap</p></th>", output);
        Assert.Contains("<td colspan=\"1\" rowspan=\"1\"><p>15 min</p></td>", output);
        Assert.DoesNotContain("style=", output);
        Assert.DoesNotContain("<colgroup", output);
    }

    [Fact]
    public void Content_class_list_only_holds_sd_classes()
    {
        Assert.All(KbHtmlSanitizer.ContentClasses, c => Assert.StartsWith("sd-", c));
    }

    [Fact]
    public void Strips_data_uri_image_sources()
    {
        // Base64 images are deliberately not supported (Outlook won't render
        // them and they bloat the row); the upload endpoint is the only way
        // to embed an image. Pin that a pasted data: URI never persists.
        var output = Sanitizer.Sanitize("<img src=\"data:image/png;base64,iVBORw0KGgo=\">");
        Assert.DoesNotContain("data:image", output);
    }
}
