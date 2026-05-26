using Servicedesk.Infrastructure.Integrations.Zammad;
using Xunit;

namespace Servicedesk.Api.Tests;

public class ZammadKbAttachmentIdExtractorTests
{
    [Theory]
    [InlineData("/api/v1/attachments/253306", 253306L)]
    [InlineData("/api/v1/attachments/253306?preview=1", 253306L)]
    [InlineData("/api/v1/attachments/1", 1L)]
    [InlineData("https://zammad.example.com/api/v1/attachments/42?disposition=inline", 42L)]
    public void Extracts_numeric_id_from_zammad_attachment_urls(string url, long expected)
    {
        Assert.Equal(expected, ZammadApiClient.ExtractAttachmentIdFromUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/api/v1/tickets/123")]
    [InlineData("/api/v1/attachments/")]
    [InlineData("/api/v1/attachments/abc")]
    public void Returns_null_for_non_attachment_urls(string? url)
    {
        Assert.Null(ZammadApiClient.ExtractAttachmentIdFromUrl(url));
    }
}

public class ZammadKbHtmlRewriterTests
{
    private static readonly Guid ArticleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Empty_body_returns_empty()
    {
        var result = ZammadKbHtmlRewriter.Rewrite(
            null, ArticleId,
            new Dictionary<long, Guid>(),
            new Dictionary<string, Guid>());
        Assert.Equal(string.Empty, result.RewrittenHtml);
        Assert.Equal(0, result.RewriteCount);
    }

    [Fact]
    public void Cid_url_is_rewritten_to_local_attachment_url()
    {
        var localId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var html = "<p><img src=\"cid:foo@example.com\" alt=\"x\"/></p>";
        var result = ZammadKbHtmlRewriter.Rewrite(
            html, ArticleId,
            new Dictionary<long, Guid>(),
            new Dictionary<string, Guid> { ["foo@example.com"] = localId });
        Assert.Contains($"/api/kb/articles/{ArticleId}/attachments/{localId}", result.RewrittenHtml);
        Assert.DoesNotContain("cid:foo@example.com", result.RewrittenHtml);
        Assert.Equal(1, result.RewriteCount);
        Assert.Equal(0, result.UnresolvedCidCount);
    }

    [Fact]
    public void Unresolved_cid_is_counted_and_kept_in_body()
    {
        var html = "<img src=\"cid:missing@example.com\">";
        var result = ZammadKbHtmlRewriter.Rewrite(
            html, ArticleId,
            new Dictionary<long, Guid>(),
            new Dictionary<string, Guid>());
        Assert.Equal(1, result.UnresolvedCidCount);
        Assert.Equal(0, result.RewriteCount);
        // The cid stays in the body so the downstream sanitizer can drop
        // the <img> entirely (its src isn't on the allow-list pattern).
        Assert.Contains("cid:missing@example.com", result.RewrittenHtml);
    }

    [Fact]
    public void Zammad_attachment_path_is_rewritten_to_local()
    {
        var localId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var html = "<img src=\"/api/v1/attachments/42\">";
        var result = ZammadKbHtmlRewriter.Rewrite(
            html, ArticleId,
            new Dictionary<long, Guid> { [42] = localId },
            new Dictionary<string, Guid>());
        Assert.Contains($"/api/kb/articles/{ArticleId}/attachments/{localId}", result.RewrittenHtml);
        Assert.DoesNotContain("/api/v1/attachments/42", result.RewrittenHtml);
        Assert.Equal(1, result.RewriteCount);
    }

    [Fact]
    public void Multiple_inline_images_rewrite_independently()
    {
        var a = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var b = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var html = """
            <p><img src="cid:a@x"></p>
            <p><img src="/api/v1/attachments/99"></p>
            <p><img src="cid:missing"></p>
            """;
        var result = ZammadKbHtmlRewriter.Rewrite(
            html, ArticleId,
            new Dictionary<long, Guid> { [99] = b },
            new Dictionary<string, Guid> { ["a@x"] = a });
        Assert.Contains(a.ToString(), result.RewrittenHtml);
        Assert.Contains(b.ToString(), result.RewrittenHtml);
        Assert.Equal(2, result.RewriteCount);
        Assert.Equal(1, result.UnresolvedCidCount);
    }
}
