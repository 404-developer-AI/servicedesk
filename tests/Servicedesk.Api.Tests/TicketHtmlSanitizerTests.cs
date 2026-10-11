using Servicedesk.Infrastructure.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class TicketHtmlSanitizerTests
{
    private readonly TicketHtmlSanitizer _sut = new();

    [Theory]
    [InlineData("<p>hi</p><script>alert(1)</script>", "script")]
    [InlineData("<img src=\"/api/x\" onerror=\"alert(1)\">", "onerror")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "iframe")]
    [InlineData("<img src=\"data:image/png;base64,iVBORw0KGgo=\">", "data:")]
    public void Strips_scriptable_and_inline_payload_content(string input, string forbidden)
    {
        var output = _sut.Sanitize(input)!;
        Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeps_composer_nodes_and_their_data_attributes()
    {
        const string html =
            "<p>Hi <span data-type=\"mention\" class=\"sd-mention\" data-id=\"u1\" data-label=\"Ann\">@Ann</span> " +
            "<span data-type=\"orderMention\" class=\"sd-order-mention\" data-order-mention=\"true\" data-order-id=\"o-42\" data-label=\"SO 42\">SO 42</span> " +
            "<span data-type=\"intakeMention\" data-intake-form=\"i-7\" data-label=\"Intake\">Intake</span></p>" +
            "<div data-sd-signature=\"\"></div>";

        var output = _sut.Sanitize(html)!;

        Assert.Contains("data-type=\"mention\"", output);
        Assert.Contains("class=\"sd-mention\"", output);
        Assert.Contains("data-order-id=\"o-42\"", output);
        Assert.Contains("data-intake-form=\"i-7\"", output);
        Assert.Contains("data-sd-signature", output);
    }

    [Fact]
    public void Keeps_relative_attachment_images_and_safe_links()
    {
        var id = Guid.NewGuid();
        var html = $"<p><img src=\"/api/tickets/{id}/attachments/{id}?inline=true\" class=\"max-w-full\"></p>" +
                   "<p><a href=\"https://example.com/a\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">link</a> " +
                   "<a href=\"mailto:someone@example.com\">mail</a></p>";

        var output = _sut.Sanitize(html)!;

        Assert.Contains($"/api/tickets/{id}/attachments/{id}", output);
        Assert.Contains("href=\"https://example.com/a\"", output);
        Assert.Contains("target=\"_blank\"", output);
        Assert.Contains("mailto:someone@example.com", output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Passes_empty_input_through(string? input) =>
        Assert.Equal(input, _sut.Sanitize(input));
}
