using Servicedesk.Infrastructure.Integrations.Claude;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins ClaudeAssistService.MarkdownToHtml — the renderer that turns the
/// model's Markdown proposal into the safe HTML subset that drops into the
/// Tiptap draft editor. Two concerns: (1) Markdown structure becomes real
/// tags (so agents no longer see literal '#', '**', '-'); (2) all text is
/// HTML-escaped and no raw markup from the model is ever trusted.
public sealed class ClaudeProposalMarkdownTests
{
    [Fact]
    public void Level_two_heading_becomes_h2()
    {
        Assert.Equal("<h2>Analyse</h2>", ClaudeAssistService.MarkdownToHtml("## Analyse"));
    }

    [Fact]
    public void Deeper_headings_clamp_to_h4()
    {
        Assert.Equal("<h4>Detail</h4>", ClaudeAssistService.MarkdownToHtml("##### Detail"));
    }

    [Fact]
    public void Bold_italic_and_inline_code_render()
    {
        Assert.Equal(
            "<p>A <strong>bold</strong> and <em>soft</em> path <code>%TEMP%</code></p>",
            ClaudeAssistService.MarkdownToHtml("A **bold** and *soft* path `%TEMP%`"));
    }

    [Fact]
    public void Bullet_list_renders_as_ul()
    {
        Assert.Equal(
            "<ul><li>first</li><li>second</li></ul>",
            ClaudeAssistService.MarkdownToHtml("- first\n- second"));
    }

    [Fact]
    public void Ordered_list_renders_as_ol()
    {
        Assert.Equal(
            "<ol><li>step one</li><li>step two</li></ol>",
            ClaudeAssistService.MarkdownToHtml("1. step one\n2. step two"));
    }

    [Fact]
    public void Fenced_code_block_preserves_and_escapes_content()
    {
        Assert.Equal(
            "<pre><code>%LOCALAPPDATA%\\Microsoft\n&lt;tag&gt;</code></pre>",
            ClaudeAssistService.MarkdownToHtml("```\n%LOCALAPPDATA%\\Microsoft\n<tag>\n```"));
    }

    [Fact]
    public void Block_quote_renders_as_blockquote()
    {
        Assert.Equal(
            "<blockquote><p>quoted line</p></blockquote>",
            ClaudeAssistService.MarkdownToHtml("> quoted line"));
    }

    [Fact]
    public void Horizontal_rule_renders_as_hr()
    {
        Assert.Equal("<hr />", ClaudeAssistService.MarkdownToHtml("---"));
    }

    [Fact]
    public void Raw_html_from_model_is_escaped_not_trusted()
    {
        Assert.Equal(
            "<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>",
            ClaudeAssistService.MarkdownToHtml("<script>alert(1)</script>"));
    }

    [Fact]
    public void Plain_numbers_with_spaces_are_left_untouched()
    {
        // Regression: the inline code-span sentinel must not be confused with
        // an ordinary " 3 " in prose (it would otherwise index the code list).
        Assert.Equal("<p>retry up to 3 of 5 times</p>",
            ClaudeAssistService.MarkdownToHtml("retry up to 3 of 5 times"));
    }

    [Fact]
    public void Soft_wrapped_paragraph_lines_join_with_break()
    {
        Assert.Equal("<p>line one<br />line two</p>",
            ClaudeAssistService.MarkdownToHtml("line one\nline two"));
    }

    [Fact]
    public void Empty_input_yields_empty_paragraph()
    {
        Assert.Equal("<p></p>", ClaudeAssistService.MarkdownToHtml(""));
    }
}
