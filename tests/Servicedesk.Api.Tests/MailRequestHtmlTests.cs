using Servicedesk.Infrastructure.Triggers.FirstOpenGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the image-stripping applied to inbound-mail HTML before it lands in
/// the first-open gate's original-request panel. Inline cid: images can't
/// resolve there and remote images would ping external trackers, so every
/// img tag (and responsive picture/source shell) must go while the
/// surrounding text and markup stay intact.
public class MailRequestHtmlTests
{
    [Fact]
    public void StripImages_RemovesImgTags_KeepsSurroundingMarkup()
    {
        var html = """<p>Hello</p><img src="cid:logo@mail" alt="logo"><p>Bye</p>""";

        var result = MailRequestHtml.StripImages(html);

        Assert.Equal("<p>Hello</p><p>Bye</p>", result);
    }

    [Fact]
    public void StripImages_RemovesSelfClosingAndRemoteImages()
    {
        var html = """before <IMG SRC="https://tracker.example/pixel.png" /> after""";

        var result = MailRequestHtml.StripImages(html);

        Assert.DoesNotContain("tracker.example", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void StripImages_RemovesPictureAndSourceWrappers()
    {
        var html = """<picture><source srcset="a.webp" type="image/webp"><img src="a.png"></picture><span>text</span>""";

        var result = MailRequestHtml.StripImages(html);

        Assert.Equal("<span>text</span>", result);
    }

    [Fact]
    public void StripImages_LeavesImagelessHtmlUntouched()
    {
        var html = "<p>Just <strong>text</strong> and a <a href=\"https://example.com\">link</a>.</p>";

        Assert.Equal(html, MailRequestHtml.StripImages(html));
    }

    [Fact]
    public void StripImages_HandlesEmptyInput()
    {
        Assert.Equal(string.Empty, MailRequestHtml.StripImages(string.Empty));
    }
}
