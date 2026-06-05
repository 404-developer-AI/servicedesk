using Servicedesk.Domain.Signatures;
using Servicedesk.Infrastructure.Signatures;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.61 — import bundle asset remap: every static image reference must follow
/// to its freshly-created asset id, a dangling reference is left as-is, and the
/// per-sender photo variable is never rewritten.
public sealed class SignatureAssetRemapTests
{
    private static SignatureDesign DesignWith(params SignatureBlock[] blocks) => new()
    {
        Rows = new[]
        {
            new SignatureRow { Columns = new[] { new SignatureColumn { Blocks = blocks } } },
        },
    };

    [Fact]
    public void Remaps_image_contactline_and_social_asset_ids()
    {
        var design = DesignWith(
            new SignatureBlock { Type = "image", AssetId = "old-logo" },
            new SignatureBlock { Type = "contactline", Html = "x", AssetId = "old-icon" },
            new SignatureBlock
            {
                Type = "social",
                Social = new[]
                {
                    new SignatureSocialItem { Network = "linkedin", Url = "https://x", AssetId = "old-li" },
                },
            });

        var map = new Dictionary<string, string>
        {
            ["old-logo"] = "new-logo",
            ["old-icon"] = "new-icon",
            ["old-li"] = "new-li",
        };

        var result = SignatureAssetRemap.Remap(design, map);
        var blocks = result.Rows[0].Columns[0].Blocks;

        Assert.Equal("new-logo", blocks[0].AssetId);
        Assert.Equal("new-icon", blocks[1].AssetId);
        Assert.Equal("new-li", blocks[2].Social![0].AssetId);
    }

    [Fact]
    public void Leaves_unmapped_reference_untouched()
    {
        var design = DesignWith(new SignatureBlock { Type = "image", AssetId = "orphan" });
        var result = SignatureAssetRemap.Remap(design, new Dictionary<string, string>());
        Assert.Equal("orphan", result.Rows[0].Columns[0].Blocks[0].AssetId);
    }

    [Fact]
    public void Never_rewrites_the_photo_variable()
    {
        // The {{agent.photo}} block carries no AssetId — only a Variable — so it
        // must survive a remap untouched (resolved per sender on the target).
        var design = DesignWith(new SignatureBlock { Type = "image", Variable = "agent.photo" });
        var result = SignatureAssetRemap.Remap(
            design, new Dictionary<string, string> { ["whatever"] = "x" });

        var block = result.Rows[0].Columns[0].Blocks[0];
        Assert.Null(block.AssetId);
        Assert.Equal("agent.photo", block.Variable);
    }
}
