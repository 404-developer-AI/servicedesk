using Servicedesk.Infrastructure.KnowledgeBase;
using Xunit;

namespace Servicedesk.Api.Tests;

public class KbBodyStripperTests
{
    [Fact]
    public void Empty_input_returns_empty_string()
    {
        Assert.Equal(string.Empty, KbBodyStripper.HtmlToText(null));
        Assert.Equal(string.Empty, KbBodyStripper.HtmlToText(""));
    }

    [Fact]
    public void Strips_basic_tags()
    {
        var html = "<p>Hello <strong>world</strong></p>";
        Assert.Equal("Hello world", KbBodyStripper.HtmlToText(html));
    }

    [Fact]
    public void Drops_script_and_style_content_entirely()
    {
        var html = "<p>visible</p><script>alert(1)</script><style>p{color:red}</style>";
        var output = KbBodyStripper.HtmlToText(html);
        Assert.Contains("visible", output);
        Assert.DoesNotContain("alert", output);
        Assert.DoesNotContain("color:red", output);
    }

    [Fact]
    public void Block_boundaries_become_newlines()
    {
        var html = "<p>first</p><p>second</p><p>third</p>";
        var output = KbBodyStripper.HtmlToText(html);
        Assert.Contains("first", output);
        Assert.Contains("second", output);
        Assert.Contains("third", output);
        // Newline-separated for FTS token boundaries.
        var lines = output.Split('\n', global::System.StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 3);
    }

    [Fact]
    public void Decodes_html_entities()
    {
        var html = "<p>caf&eacute; &amp; tea &nbsp;here</p>";
        var output = KbBodyStripper.HtmlToText(html);
        Assert.Contains("café", output);
        Assert.Contains("&", output);
        Assert.DoesNotContain("&nbsp;", output);
    }

    [Fact]
    public void Collapses_runs_of_whitespace_within_a_line()
    {
        var html = "<p>spaced     out    text</p>";
        var output = KbBodyStripper.HtmlToText(html);
        Assert.Equal("spaced out text", output);
    }

    [Fact]
    public void Br_tag_creates_a_newline()
    {
        var html = "first<br>second<br/>third";
        var output = KbBodyStripper.HtmlToText(html);
        Assert.Contains("first", output);
        Assert.Contains("second", output);
        Assert.Contains("third", output);
        Assert.Contains('\n', output);
    }
}
