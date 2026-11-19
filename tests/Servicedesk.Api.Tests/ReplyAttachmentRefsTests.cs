using Servicedesk.Infrastructure.Tickets.NoteThreads;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.58 — which files a reply body references: only download URLs of
/// the reply's own ticket count (images and links alike), each once.
public sealed class ReplyAttachmentRefsTests
{
    private static readonly Guid Ticket = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Extracts_own_ticket_images_and_links_once()
    {
        var html = $"<p><img src=\"/api/tickets/{Ticket}/attachments/{A}?inline=true\"></p>"
            + $"<p><a href=\"/api/tickets/{Ticket}/attachments/{B}\">log.txt</a></p>"
            + $"<p><img src=\"/api/tickets/{Ticket}/attachments/{A}\"></p>"
            + $"<p><img src=\"/api/tickets/{Other}/attachments/{B}\"></p>";

        Assert.Equal(new[] { A, B }, ReplyAttachmentRefs.Extract(html, Ticket));
    }

    [Theory]
    [InlineData("<p><img src=\"/x.png\"></p>", true)]
    [InlineData("<p><IMG alt=\"\" src='/x.png'></p>", true)]
    [InlineData("<p>no image</p>", false)]
    [InlineData("<p><img alt=\"no source\"></p>", false)]
    public void Detects_an_image(string html, bool expected)
        => Assert.Equal(expected, ReplyAttachmentRefs.HasImage(html));
}
