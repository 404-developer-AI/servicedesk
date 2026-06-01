using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Pure helper that relinks the Zammad-side inline-image URLs in an imported
/// ticket article body so they point at our local ticket-attachment endpoint
/// after import.
///
/// Zammad's web editor writes attachment references straight into the article
/// HTML as absolute API paths — the per-article form
/// <c>/api/v1/ticket_attachment/{ticketId}/{articleId}/{attachmentId}</c> and,
/// less commonly, the polymorphic <c>/api/v1/attachments/{storeId}</c>. Left
/// untouched, those resolve against OUR origin on render and fire a burst of
/// requests at a path this app doesn't serve — enough, on a content-rich
/// ticket, to trip the global rate limiter and flood the audit log. We rewrite
/// them at import time (the only point where the Zammad-attachment-id →
/// local-attachment-id mapping is known) to
/// <c>/api/tickets/{ticketId}/attachments/{localId}</c>, which the generic
/// ticket-attachment download endpoint serves for every imported attachment
/// (event_id is always stamped, so Mail- and Ticket-owned rows both resolve).
///
/// <c>cid:</c> references are intentionally NOT handled here — those belong to
/// email articles and are rewritten on the read path by
/// <c>MailTimelineEnricher</c>; touching them here would only duplicate that.
///
/// Unresolved references (an id with no matching local attachment — e.g. a
/// too-large attachment that was skipped during import) are left as-is so the
/// downstream sanitizer can strip them; the count is returned for the
/// import-record mapping JSON.
public static partial class ZammadTicketHtmlRewriter
{
    /// Rewrite the body. <paramref name="attachmentMap"/> maps a Zammad
    /// attachment-id → the local attachment-id (Guid) inserted for it.
    public static ZammadTicketHtmlRewriteResult Rewrite(
        string? bodyHtml,
        Guid ticketId,
        IReadOnlyDictionary<long, Guid> attachmentMap)
    {
        if (string.IsNullOrEmpty(bodyHtml) || attachmentMap.Count == 0)
        {
            return new ZammadTicketHtmlRewriteResult(bodyHtml ?? string.Empty, 0, 0);
        }

        var rewriteCount = 0;
        var unresolved = 0;

        string ResolveLast(Match m, int idGroup)
        {
            if (long.TryParse(m.Groups[idGroup].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var zammadAttId)
                && attachmentMap.TryGetValue(zammadAttId, out var localId))
            {
                rewriteCount++;
                return BuildLocalUrl(ticketId, localId);
            }
            unresolved++;
            return m.Value;
        }

        // /api/v1/ticket_attachment/{ticketId}/{articleId}/{attachmentId}
        var rewritten = TicketAttachmentPathPattern().Replace(bodyHtml, m => ResolveLast(m, 3));
        // /api/v1/attachments/{storeId}
        rewritten = AttachmentPathPattern().Replace(rewritten, m => ResolveLast(m, 1));

        return new ZammadTicketHtmlRewriteResult(rewritten, rewriteCount, unresolved);
    }

    private static string BuildLocalUrl(Guid ticketId, Guid attachmentId)
        => $"/api/tickets/{ticketId}/attachments/{attachmentId}";

    [GeneratedRegex(@"/api/v1/ticket_attachment/(\d+)/(\d+)/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TicketAttachmentPathPattern();

    [GeneratedRegex(@"/api/v1/attachments/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPathPattern();
}

public sealed record ZammadTicketHtmlRewriteResult(
    string RewrittenHtml,
    int RewriteCount,
    int UnresolvedCount);
