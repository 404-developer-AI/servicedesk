using Servicedesk.Domain.Signatures;
using Servicedesk.Infrastructure.Signatures;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.58 — locks the send-time render contract: tokens substitute, a line
/// whose only token resolves empty collapses, static text always survives,
/// images become a single inline cid part, and an unresolved photo variable
/// renders nothing rather than a broken image.
public sealed class SignatureRendererTests
{
    private static SignatureRenderer NewRenderer() => new(new SignatureHtmlSanitizer());

    private static SignatureDesign DesignWith(params SignatureBlock[] blocks) => new()
    {
        Rows = new[]
        {
            new SignatureRow { Columns = new[] { new SignatureColumn { Blocks = blocks } } },
        },
    };

    private static SignatureVariables Vars(
        string fullName = "", string jobTitle = "", string phone = "",
        string mobile = "", string email = "", string? photoHash = null) =>
        new(fullName, fullName.Split(' ')[0], "", jobTitle, email, phone, mobile, photoHash, photoHash is null ? null : "image/jpeg");

    [Fact]
    public void Substitutes_tokens_and_keeps_static_text()
    {
        var design = DesignWith(new SignatureBlock { Type = "text", Html = "{{agent.fullName}}<br>Bestuurder" });
        var html = NewRenderer().Render(design, Vars(fullName: "Christiaan Keyers"), Array.Empty<SignatureAsset>()).Html;

        Assert.Contains("Christiaan Keyers", html);
        Assert.Contains("Bestuurder", html);
        Assert.DoesNotContain("{{agent.fullName}}", html);
    }

    [Fact]
    public void Collapses_a_line_whose_only_token_is_empty()
    {
        var design = DesignWith(new SignatureBlock
        {
            Type = "text",
            Html = "Tel: {{agent.phone}}<br>Mob: {{agent.mobile}}",
        });
        // phone present, mobile empty → the "Mob:" line must disappear.
        var html = NewRenderer().Render(design, Vars(phone: "+32 89 39 93 92"), Array.Empty<SignatureAsset>()).Html;

        Assert.Contains("Tel:", html);
        Assert.Contains("+32 89 39 93 92", html);
        Assert.DoesNotContain("Mob:", html);
    }

    [Fact]
    public void Image_asset_becomes_single_inline_cid_part()
    {
        var assetId = Guid.NewGuid();
        var asset = new SignatureAsset(assetId, Guid.NewGuid(), "deadbeefhash", "image/png", "logo.png", 1234, DateTime.UtcNow);
        var design = DesignWith(new SignatureBlock { Type = "image", AssetId = assetId.ToString(), WidthPx = 120 });

        var rendered = NewRenderer().Render(design, Vars(), new[] { asset });

        Assert.Single(rendered.Assets);
        Assert.Equal("deadbeefhash", rendered.Assets[0].ContentHash);
        Assert.Contains($"cid:{rendered.Assets[0].Cid}", rendered.Html);
    }

    [Fact]
    public void Missing_photo_variable_renders_nothing()
    {
        var design = DesignWith(new SignatureBlock { Type = "image", Variable = "agent.photo", WidthPx = 64 });
        var rendered = NewRenderer().Render(design, Vars(photoHash: null), Array.Empty<SignatureAsset>());

        Assert.Empty(rendered.Assets);
        Assert.DoesNotContain("<img", rendered.Html);
    }

    [Fact]
    public void Html_in_a_token_value_is_encoded_not_injected()
    {
        var design = DesignWith(new SignatureBlock { Type = "text", Html = "{{agent.jobTitle}}" });
        var rendered = NewRenderer().Render(design, Vars(jobTitle: "<script>x</script>"), Array.Empty<SignatureAsset>());

        Assert.DoesNotContain("<script>x</script>", rendered.Html);
        Assert.Contains("&lt;script&gt;", rendered.Html);
    }

    // v0.0.61 — compose-preview render path: images inline as self-contained
    // data: URIs (no cid parts) so the in-app preview needs no authenticated
    // fetch. The send-time cid contract above is unchanged.
    [Fact]
    public void Image_asset_inlines_as_data_uri_in_preview_mode()
    {
        var assetId = Guid.NewGuid();
        var asset = new SignatureAsset(assetId, Guid.NewGuid(), "deadbeefhash", "image/png", "logo.png", 3, DateTime.UtcNow);
        var design = DesignWith(new SignatureBlock { Type = "image", AssetId = assetId.ToString(), WidthPx = 120 });
        var bytes = new Dictionary<string, InlineImageBytes>(StringComparer.Ordinal)
        {
            ["deadbeefhash"] = new InlineImageBytes("image/png", new byte[] { 1, 2, 3 }),
        };

        var rendered = NewRenderer().Render(design, Vars(), new[] { asset }, bytes);

        Assert.Empty(rendered.Assets); // no cid parts in preview mode
        Assert.Contains("data:image/png;base64,AQID", rendered.Html);
        Assert.DoesNotContain("cid:", rendered.Html);
    }

    [Fact]
    public void Image_with_missing_bytes_collapses_in_preview_mode()
    {
        var assetId = Guid.NewGuid();
        var asset = new SignatureAsset(assetId, Guid.NewGuid(), "missinghash", "image/png", "logo.png", 3, DateTime.UtcNow);
        var design = DesignWith(new SignatureBlock { Type = "image", AssetId = assetId.ToString(), WidthPx = 120 });
        // Empty byte map → the image has no resolvable source and must collapse
        // rather than emit a broken <img>.
        var bytes = new Dictionary<string, InlineImageBytes>(StringComparer.Ordinal);

        var rendered = NewRenderer().Render(design, Vars(), new[] { asset }, bytes);

        Assert.DoesNotContain("<img", rendered.Html);
    }
}
