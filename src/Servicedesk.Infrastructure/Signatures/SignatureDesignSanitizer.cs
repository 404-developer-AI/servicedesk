using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

/// Walks a posted design and sanitizes the user-authored HTML in every
/// text/disclaimer block before it is stored — defence-in-depth alongside the
/// render-time sanitize. The block-structure fields (image/divider/spacer/
/// social) carry no free HTML, so only the Html property is rewritten.
public static class SignatureDesignSanitizer
{
    public static SignatureDesign Sanitize(SignatureDesign design, ISignatureHtmlSanitizer sanitizer)
    {
        var rows = (design.Rows ?? Array.Empty<SignatureRow>())
            .Select(row => row with
            {
                Columns = (row.Columns ?? Array.Empty<SignatureColumn>())
                    .Select(col => col with
                    {
                        Blocks = (col.Blocks ?? Array.Empty<SignatureBlock>())
                            .Select(block => SanitizeBlock(block, sanitizer))
                            .ToList(),
                    })
                    .ToList(),
            })
            .ToList();

        return design with { Rows = rows };
    }

    private static SignatureBlock SanitizeBlock(SignatureBlock block, ISignatureHtmlSanitizer sanitizer)
    {
        var type = (block.Type ?? "text").ToLowerInvariant();
        if (type is "text" or "disclaimer" && !string.IsNullOrWhiteSpace(block.Html))
            return block with { Html = sanitizer.Sanitize(block.Html) };
        return block;
    }
}
