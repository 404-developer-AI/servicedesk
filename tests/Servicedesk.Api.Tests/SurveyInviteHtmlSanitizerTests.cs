using Servicedesk.Infrastructure.Surveys;
using Xunit;

namespace Servicedesk.Api.Tests;

public class SurveyInviteHtmlSanitizerTests
{
    private static readonly SurveyInviteHtmlSanitizer Sanitizer = new();

    [Fact]
    public void Empty_input_returns_empty_string()
    {
        Assert.Equal(string.Empty, Sanitizer.Sanitize(null));
        Assert.Equal(string.Empty, Sanitizer.Sanitize(""));
        Assert.Equal(string.Empty, Sanitizer.Sanitize("   "));
    }

    [Fact]
    public void Preserves_survey_link_token_href()
    {
        // The whole reason this sanitizer exists: the stock URL allow-list
        // drops a non-URL href, which silently broke the survey link.
        var output = Sanitizer.Sanitize("<a href=\"{{survey.link}}\">Open the survey</a>");
        Assert.Contains("href=\"{{survey.link}}\"", output);
        Assert.Contains("Open the survey", output);
    }

    [Fact]
    public void Preserves_arbitrary_token_href()
    {
        var output = Sanitizer.Sanitize("<a href=\"{{ticket.number}}\">x</a>");
        Assert.Contains("href=\"{{ticket.number}}\"", output);
    }

    [Fact]
    public void Still_strips_javascript_urls()
    {
        var output = Sanitizer.Sanitize("<a href=\"javascript:alert(1)\">click</a>");
        Assert.DoesNotContain("javascript:", output);
    }

    [Fact]
    public void Still_strips_script_tags()
    {
        var output = Sanitizer.Sanitize("<p>safe</p><script>alert('xss')</script>");
        Assert.Contains("safe", output);
        Assert.DoesNotContain("<script", output);
    }

    [Fact]
    public void Allows_real_https_links()
    {
        var output = Sanitizer.Sanitize("<a href=\"https://example.com\">x</a>");
        Assert.Contains("https://example.com", output);
    }

    [Fact]
    public void Allows_basic_formatting()
    {
        var input = "<p><strong>bold</strong></p><p><em>italic</em></p><ul><li>a</li></ul>";
        var output = Sanitizer.Sanitize(input);
        Assert.Contains("<strong>", output);
        Assert.Contains("<em>", output);
        Assert.Contains("<ul>", output);
        // Two paragraphs survive — this is what makes the recipient's mail
        // keep its line breaks instead of collapsing into one block.
        Assert.Contains("<p>", output);
    }

    [Fact]
    public void A_token_that_is_not_a_pure_placeholder_is_still_dropped()
    {
        // Defense: only a clean {{token}} href is re-admitted. A scheme
        // smuggled alongside a token must not slip through.
        var output = Sanitizer.Sanitize("<a href=\"javascript:{{x}}\">x</a>");
        Assert.DoesNotContain("javascript:", output);
    }
}
