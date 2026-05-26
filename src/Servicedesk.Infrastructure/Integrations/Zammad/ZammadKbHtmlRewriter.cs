using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Pure helper that rewrites the Zammad-side image URLs in a KB-answer's
/// HTML body so they point at our local KB-attachment endpoint after
/// import. Two rewrite paths are supported:
///
/// <list type="bullet">
/// <item><b>cid: scheme</b> — Zammad uses <c>cid:&lt;contentId&gt;</c> for
/// inline images authored from email. The rewriter resolves cid → local
/// attachment-id via the supplied lookup and emits
/// <c>/api/kb/articles/{articleId}/attachments/{aid}</c>.</item>
/// <item><b>preview/url path</b> — Zammad's web editor writes paths like
/// <c>/api/v1/attachments/{store_id}</c> directly into the body. The
/// rewriter resolves those by matching against the answer's attachment
/// manifest (preview_url field) and re-points to the local download URL.</item>
/// </list>
///
/// The rewriter is intentionally separate from the HTML sanitizer — it
/// runs <i>before</i> sanitizer so the sanitizer's allow-list pass sees
/// the already-rewritten src and accepts the local URL pattern.
///
/// Output is a <see cref="ZammadKbHtmlRewriteResult"/> with the rewritten
/// HTML plus per-rewrite warnings (unresolved cid, unresolved preview-url).
/// Unresolved references are kept as-is in the output so the sanitizer
/// can strip them — the warnings surface in the import-record mapping
/// JSONB so the admin sees the count.
public static partial class ZammadKbHtmlRewriter
{
    /// Rewrite the body. <paramref name="attachmentMap"/> maps Zammad
    /// attachment-id → local attachment-id (Guid). <paramref name="cidMap"/>
    /// maps cid-token → local attachment-id; cid lookups are case-
    /// insensitive (RFC 2392 treats the token as case-insensitive).
    public static ZammadKbHtmlRewriteResult Rewrite(
        string? bodyHtml,
        Guid articleId,
        IReadOnlyDictionary<long, Guid> attachmentMap,
        IReadOnlyDictionary<string, Guid> cidMap)
    {
        if (string.IsNullOrEmpty(bodyHtml))
        {
            return new ZammadKbHtmlRewriteResult(string.Empty, 0, 0, 0);
        }

        var unresolvedCid = 0;
        var unresolvedPreview = 0;
        var rewriteCount = 0;

        // cid: → local URL
        var rewritten = CidPattern().Replace(bodyHtml, m =>
        {
            var token = m.Groups[1].Value.Trim('<', '>', ' ');
            if (cidMap.TryGetValue(token, out var localId))
            {
                rewriteCount++;
                return BuildLocalUrl(articleId, localId);
            }
            unresolvedCid++;
            return m.Value;
        });

        // Zammad's editor-produced /api/v1/attachments/{id} (or
        // /api/v1/attachments/{store_id}) — resolve by matching the
        // upstream id against the answer's attachment-manifest.
        rewritten = AttachmentPathPattern().Replace(rewritten, m =>
        {
            if (long.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var upstreamId)
                && attachmentMap.TryGetValue(upstreamId, out var localId))
            {
                rewriteCount++;
                return BuildLocalUrl(articleId, localId);
            }
            unresolvedPreview++;
            return m.Value;
        });

        return new ZammadKbHtmlRewriteResult(
            RewrittenHtml: rewritten,
            RewriteCount: rewriteCount,
            UnresolvedCidCount: unresolvedCid,
            UnresolvedPreviewCount: unresolvedPreview);
    }

    private static string BuildLocalUrl(Guid articleId, Guid attachmentId)
        => $"/api/kb/articles/{articleId}/attachments/{attachmentId}";

    [GeneratedRegex("cid:([A-Za-z0-9._%+\\-@<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CidPattern();

    [GeneratedRegex("/api/v1/attachments/(\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPathPattern();
}

public sealed record ZammadKbHtmlRewriteResult(
    string RewrittenHtml,
    int RewriteCount,
    int UnresolvedCidCount,
    int UnresolvedPreviewCount);
