using Servicedesk.Infrastructure.Integrations.Zammad;
using Xunit;

namespace Servicedesk.Api.Tests;

public class ZammadTicketHtmlRewriterTests
{
    private static readonly Guid TicketId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void Empty_body_returns_unchanged()
    {
        var result = ZammadTicketHtmlRewriter.Rewrite(
            null, TicketId, new Dictionary<long, Guid> { [1] = Guid.NewGuid() });
        Assert.Equal(string.Empty, result.RewrittenHtml);
        Assert.Equal(0, result.RewriteCount);
    }

    [Fact]
    public void Empty_map_leaves_body_untouched()
    {
        var html = "<img src=\"/api/v1/ticket_attachment/12948/120112/266285\">";
        var result = ZammadTicketHtmlRewriter.Rewrite(html, TicketId, new Dictionary<long, Guid>());
        Assert.Equal(html, result.RewrittenHtml);
        Assert.Equal(0, result.RewriteCount);
    }

    [Fact]
    public void Ticket_attachment_path_is_rewritten_to_local_url()
    {
        var localId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        // The last path segment (266285) is the Zammad attachment id.
        var html = "<p><img src=\"/api/v1/ticket_attachment/12948/120112/266285\" alt=\"x\"></p>";
        var result = ZammadTicketHtmlRewriter.Rewrite(
            html, TicketId, new Dictionary<long, Guid> { [266285] = localId });

        Assert.Contains($"/api/tickets/{TicketId}/attachments/{localId}", result.RewrittenHtml);
        Assert.DoesNotContain("/api/v1/ticket_attachment/", result.RewrittenHtml);
        Assert.Equal(1, result.RewriteCount);
        Assert.Equal(0, result.UnresolvedCount);
    }

    [Fact]
    public void Polymorphic_attachments_path_is_rewritten()
    {
        var localId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var html = "<img src=\"/api/v1/attachments/42\">";
        var result = ZammadTicketHtmlRewriter.Rewrite(
            html, TicketId, new Dictionary<long, Guid> { [42] = localId });

        Assert.Contains($"/api/tickets/{TicketId}/attachments/{localId}", result.RewrittenHtml);
        Assert.DoesNotContain("/api/v1/attachments/42", result.RewrittenHtml);
        Assert.Equal(1, result.RewriteCount);
    }

    [Fact]
    public void Unresolved_id_is_counted_and_left_in_body()
    {
        // A skipped (e.g. too-large) attachment has no local row — the URL
        // stays so the downstream sanitizer can drop the <img>.
        var html = "<img src=\"/api/v1/ticket_attachment/12948/120112/999999\">";
        var result = ZammadTicketHtmlRewriter.Rewrite(
            html, TicketId, new Dictionary<long, Guid> { [266285] = Guid.NewGuid() });

        Assert.Equal(0, result.RewriteCount);
        Assert.Equal(1, result.UnresolvedCount);
        Assert.Contains("/api/v1/ticket_attachment/12948/120112/999999", result.RewrittenHtml);
    }

    [Fact]
    public void Multiple_images_rewrite_independently()
    {
        var a = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var b = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var html = """
            <p><img src="/api/v1/ticket_attachment/12948/120112/100"></p>
            <p><img src="/api/v1/attachments/200"></p>
            <p><img src="/api/v1/ticket_attachment/12948/120112/300"></p>
            """;
        var result = ZammadTicketHtmlRewriter.Rewrite(
            html, TicketId,
            new Dictionary<long, Guid> { [100] = a, [200] = b });

        Assert.Contains(a.ToString(), result.RewrittenHtml);
        Assert.Contains(b.ToString(), result.RewrittenHtml);
        Assert.Equal(2, result.RewriteCount);
        Assert.Equal(1, result.UnresolvedCount);
    }

    [Fact]
    public void Cid_references_are_left_untouched()
    {
        // cid: is handled on the read path by MailTimelineEnricher; this
        // rewriter must not touch it.
        var html = "<img src=\"cid:foo@example.com\">";
        var result = ZammadTicketHtmlRewriter.Rewrite(
            html, TicketId, new Dictionary<long, Guid> { [1] = Guid.NewGuid() });

        Assert.Equal(html, result.RewrittenHtml);
        Assert.Equal(0, result.RewriteCount);
    }
}
