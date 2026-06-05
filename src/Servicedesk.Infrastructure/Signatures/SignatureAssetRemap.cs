using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

/// Rewrites a signature's block-tree so every STATIC image reference points at a
/// new asset id. Used when importing a portable bundle (v0.0.61): the bundle's
/// assets are re-created on the target install with fresh ids, and the design's
/// references (image/contactline blocks via <c>AssetId</c>, and social-item
/// icons) must follow. References with no mapping (a dangling id) are left as-is
/// and collapse harmlessly at render time. The per-sender <c>{{agent.photo}}</c>
/// variable is not an asset, so it is untouched.
public static class SignatureAssetRemap
{
    public static SignatureDesign Remap(SignatureDesign design, IReadOnlyDictionary<string, string> idMap)
    {
        string? MapId(string? id) =>
            !string.IsNullOrEmpty(id) && idMap.TryGetValue(id, out var next) ? next : id;

        SignatureBlock MapBlock(SignatureBlock b) => b with
        {
            AssetId = MapId(b.AssetId),
            Social = b.Social?.Select(s => s with { AssetId = MapId(s.AssetId) }).ToList(),
        };

        return design with
        {
            Rows = (design.Rows ?? Array.Empty<SignatureRow>())
                .Select(r => r with
                {
                    Columns = (r.Columns ?? Array.Empty<SignatureColumn>())
                        .Select(c => c with
                        {
                            Blocks = (c.Blocks ?? Array.Empty<SignatureBlock>()).Select(MapBlock).ToList(),
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }
}
