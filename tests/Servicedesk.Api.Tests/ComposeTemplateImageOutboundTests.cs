using Servicedesk.Infrastructure.Mail.Outbound;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.92 — pins the URL contract for inline mail-template images. The
/// upload endpoint returns `/api/compose-templates/images/{id}`, the admin
/// editor embeds it in the template body, and at send time
/// <see cref="OutboundMailService"/> extracts those ids to copy each image
/// onto the ticket and cid-embed it. If extraction and the URL builder ever
/// drift apart, template images silently stop rendering for recipients —
/// these tests make that a compile-time-adjacent failure instead.
public sealed class ComposeTemplateImageOutboundTests
{
    private static readonly Guid ImageA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ImageB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void Url_builder_and_extractor_agree()
    {
        var url = OutboundMailService.ComposeTemplateImageUrl(ImageA);

        var ids = OutboundMailService.ExtractComposeTemplateImageIds($"<img src=\"{url}\">");

        Assert.Equal(new[] { ImageA }, ids);
    }

    [Fact]
    public void Extracts_multiple_ids_in_first_appearance_order()
    {
        var body =
            $"<p>hi</p><img src=\"/api/compose-templates/images/{ImageB}\">" +
            $"<img src=\"/api/compose-templates/images/{ImageA}\">";

        var ids = OutboundMailService.ExtractComposeTemplateImageIds(body);

        Assert.Equal(new[] { ImageB, ImageA }, ids);
    }

    [Fact]
    public void Duplicate_references_yield_one_id()
    {
        var url = OutboundMailService.ComposeTemplateImageUrl(ImageA);
        var body = $"<img src=\"{url}\"><p>text</p><img src=\"{url}\">";

        var ids = OutboundMailService.ExtractComposeTemplateImageIds(body);

        Assert.Equal(new[] { ImageA }, ids);
    }

    [Fact]
    public void Uppercase_guid_and_path_still_match()
    {
        // Our own editor emits lowercase, but the body may round-trip through
        // clients that alter casing; the rewrite is case-insensitive end-to-end.
        var body = $"<img src=\"/API/COMPOSE-TEMPLATES/IMAGES/{ImageA.ToString().ToUpperInvariant()}\">";

        var ids = OutboundMailService.ExtractComposeTemplateImageIds(body);

        Assert.Equal(new[] { ImageA }, ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<p>no images at all</p>")]
    [InlineData("<img src=\"/api/tickets/11111111-2222-3333-4444-555555555555/attachments/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\">")]
    [InlineData("<img src=\"/api/compose-templates/images/not-a-guid\">")]
    public void Non_template_image_bodies_yield_nothing(string body)
    {
        Assert.Empty(OutboundMailService.ExtractComposeTemplateImageIds(body));
    }
}
