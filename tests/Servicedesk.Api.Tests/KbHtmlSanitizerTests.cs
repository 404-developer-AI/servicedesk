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
}
