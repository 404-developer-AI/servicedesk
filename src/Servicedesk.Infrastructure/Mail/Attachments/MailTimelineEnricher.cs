using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Mail.Attachments;

public sealed class MailTimelineEnricher : IMailTimelineEnricher
{
    private readonly IMailTimelineLookup _lookup;
    private readonly IBlobStore _blobs;
    private readonly ILogger<MailTimelineEnricher> _logger;

    public MailTimelineEnricher(
        IMailTimelineLookup lookup,
        IBlobStore blobs,
        ILogger<MailTimelineEnricher> logger)
    {
        _lookup = lookup;
        _blobs = blobs;
        _logger = logger;
    }

    public async Task<TicketDetail> EnrichAsync(TicketDetail detail, CancellationToken ct)
    {
        if (detail.Events.Count == 0) return detail;

        // v0.0.101 — pass 1: collect every id the timeline needs, then load
        // them in ONE batched round-trip. Before this the loop below issued
        // 2–3 queries per inbound mail and 1–3 per note/comment/sent mail, so
        // opening a long-running ticket cost O(events) round-trips.
        var mailIds = new List<Guid>();
        var eventIds = new List<long>();
        var sentEventIds = new List<long>();
        foreach (var evt in detail.Events)
        {
            switch (evt.EventType)
            {
                case "MailReceived":
                    if (TryGetMailMessageId(evt.MetadataJson) is { } mid) mailIds.Add(mid);
                    break;
                case "MailSent":
                    eventIds.Add(evt.Id);
                    sentEventIds.Add(evt.Id);
                    break;
                case "Note":
                case "Comment":
                    eventIds.Add(evt.Id);
                    break;
            }
        }
        if (mailIds.Count == 0 && eventIds.Count == 0) return detail;

        MailTimelineBatch batch;
        try
        {
            batch = await _lookup.LoadAsync(mailIds, eventIds, sentEventIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Enrichment is a read-time nicety; the timeline must still render.
            _logger.LogWarning(ex,
                "MailTimelineEnricher batch load failed for ticket {TicketId} — rendering timeline unenriched.",
                detail.Ticket.Id);
            return detail;
        }

        // pass 2: same per-event enrichment as before, now from in-memory lookups.
        var enriched = new List<TicketEvent>(detail.Events.Count);
        foreach (var evt in detail.Events)
        {
            // Inbound mail — full enrichment with cid-rewrite + recipient
            // metadata. Mail-message id lives in the metadata so we can join
            // to mail_messages + attachments efficiently.
            if (evt.EventType == "MailReceived")
            {
                var mailId = TryGetMailMessageId(evt.MetadataJson);
                enriched.Add(mailId is null
                    ? evt
                    : await TryEnrichMailReceivedAsync(detail.Ticket.Id, mailId.Value, evt, batch, ct));
                continue;
            }

            // Note / Comment / outbound mail (MailSent) — attachments are
            // linked to the event via attachments.event_id. Outbound-mail
            // bodies already use /api/.../attachments/{id} URLs (the editor
            // view), so there's no cid-rewrite to do; just surface the
            // non-inline rows in metadata for the timeline-strip.
            if (evt.EventType == "Note" || evt.EventType == "Comment" || evt.EventType == "MailSent")
            {
                var e = TryAppendEventAttachments(detail.Ticket.Id, evt, batch);
                // Outbound mail also gets the From/To/Cc/Bcc header surfaced.
                // Its metadata carries no mail_message_id (unlike inbound), so
                // the mail row is found via ticket_event_id. Runs after the
                // attachment step and preserves whatever it injected.
                if (evt.EventType == "MailSent")
                    e = TryAppendMailSentHeaders(e, batch);
                enriched.Add(e);
                continue;
            }

            enriched.Add(evt);
        }

        return detail with { Events = enriched };
    }

    private async Task<TicketEvent> TryEnrichMailReceivedAsync(
        Guid ticketId, Guid mailId, TicketEvent evt, MailTimelineBatch batch, CancellationToken ct)
    {
        try
        {
            var attachments = batch.AttachmentsByMailId[mailId].ToList();
            var readyCount = attachments.Count(a => a.ProcessingState == "Ready");
            var pendingCount = attachments.Count(a => a.ProcessingState == "Pending");
            var failedCount = attachments.Count(a => a.ProcessingState == "Failed");

            string? rewrittenHtml = null;
            int cidReplaced = 0, cidUnmatched = 0;
            batch.MailsById.TryGetValue(mailId, out var mail);
            if (mail is not null && !string.IsNullOrWhiteSpace(mail.BodyHtmlBlobHash))
            {
                await using var stream = await _blobs.OpenReadAsync(mail.BodyHtmlBlobHash, ct);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    var html = await reader.ReadToEndAsync(ct);
                    rewrittenHtml = RewriteCidReferences(html, ticketId, mailId, attachments, out cidReplaced, out cidUnmatched);
                }
            }

            // Debug-level on purpose: this fires for every inbound mail on every
            // ticket-detail load — useful when troubleshooting attachment
            // processing, pure noise otherwise. Real failures log as warnings below.
            _logger.LogDebug(
                "[MailEnrich] ticket={TicketId} mail={MailId} attachments total={Total} ready={Ready} pending={Pending} failed={Failed} cid replaced={Replaced} unmatched={Unmatched}",
                ticketId, mailId, attachments.Count, readyCount, pendingCount, failedCount, cidReplaced, cidUnmatched);

            var recipients = batch.RecipientsByMailId[mailId].ToList();
            var newMetadata = InjectMailAttachmentsAndRecipients(
                evt.MetadataJson, ticketId, mailId,
                mail?.FromAddress, mail?.FromName, attachments, recipients);
            return evt with
            {
                BodyHtml = rewrittenHtml ?? evt.BodyHtml,
                MetadataJson = newMetadata,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MailTimelineEnricher failed for ticket {TicketId} mail {MailId} — leaving event untouched.",
                ticketId, mailId);
            return evt;
        }
    }

    private TicketEvent TryAppendMailSentHeaders(TicketEvent evt, MailTimelineBatch batch)
    {
        try
        {
            if (!batch.MailsBySentEventId.TryGetValue(evt.Id, out var mail)) return evt;
            var recipients = batch.RecipientsByMailId[mail.Id].ToList();
            var newMetadata = InjectMailHeaders(evt.MetadataJson, mail.FromAddress, mail.FromName, recipients);
            return evt with { MetadataJson = newMetadata };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MailTimelineEnricher (sent-headers) failed for event {EventId} — leaving headers off.",
                evt.Id);
            return evt;
        }
    }

    private TicketEvent TryAppendEventAttachments(
        Guid ticketId, TicketEvent evt, MailTimelineBatch batch)
    {
        try
        {
            var attachments = batch.AttachmentsByEventId[evt.Id].ToList();
            if (attachments.Count == 0) return evt;
            // Inline rows are already embedded in bodyHtml via the original
            // /api/.../attachments/{id} URL the editor produced — surfacing
            // them again as a download chip would double-render. Only the
            // non-inline rows belong in the strip.
            var visible = attachments
                .Where(a => a.ProcessingState == "Ready" && !a.IsInline)
                .ToList();
            if (visible.Count == 0) return evt;
            var newMetadata = InjectAttachmentsList(
                evt.MetadataJson,
                visible.Select(a => BuildEventAttachmentDescriptor(ticketId, a)));
            return evt with { MetadataJson = newMetadata };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MailTimelineEnricher (event-attached) failed for ticket {TicketId} event {EventId} — leaving event untouched.",
                ticketId, evt.Id);
            return evt;
        }
    }

    private static object BuildEventAttachmentDescriptor(Guid ticketId, AttachmentRow a)
    {
        // Single canonical URL for both ticket-staged and event-attached
        // rows: the generic ticket attachment endpoint authenticates by
        // ticket-membership, not by mail-membership. Inbound mail
        // attachments still use the per-mail URL set in
        // InjectMailAttachmentsAndRecipients above.
        return new
        {
            id = a.Id,
            name = a.OriginalFilename,
            mimeType = a.MimeType,
            size = a.SizeBytes,
            url = $"/api/tickets/{ticketId}/attachments/{a.Id}",
        };
    }

    private static string InjectAttachmentsList(string metadataJson, IEnumerable<object> items)
    {
        var dict = ParseMetadata(metadataJson);
        dict["attachments"] = JsonSerializer.SerializeToElement(items.ToList());
        return JsonSerializer.Serialize(dict);
    }

    private static string InjectMailAttachmentsAndRecipients(
        string metadataJson, Guid ticketId, Guid mailId,
        string? fromAddress, string? fromName,
        IReadOnlyList<AttachmentRow> attachments,
        IReadOnlyList<MailRecipientRow> recipients)
    {
        // Only non-inline Ready attachments are surfaced as download links —
        // inline images are already placed in the HTML via cid-rewrite above.
        // Failed/Pending rows are omitted; their state is not actionable in UI.
        var items = attachments
            .Where(a => !a.IsInline && a.ProcessingState == "Ready")
            .Select(a => new
            {
                id = a.Id,
                name = a.OriginalFilename,
                mimeType = a.MimeType,
                size = a.SizeBytes,
                url = $"/api/tickets/{ticketId}/mail/{mailId}/attachments/{a.Id}",
            })
            .ToList();

        var dict = ParseMetadata(metadataJson);
        dict["attachments"] = JsonSerializer.SerializeToElement(items);
        AddMailHeaders(dict, fromAddress, fromName, recipients);
        return JsonSerializer.Serialize(dict);
    }

    private static string InjectMailHeaders(
        string metadataJson, string? fromAddress, string? fromName,
        IReadOnlyList<MailRecipientRow> recipients)
    {
        var dict = ParseMetadata(metadataJson);
        AddMailHeaders(dict, fromAddress, fromName, recipients);
        return JsonSerializer.Serialize(dict);
    }

    // Writes the From/To/Cc/Bcc header fields the timeline's mail-header panel
    // reads. `from`/`fromName` are plain strings, matching the shape inbound
    // events already carry from ingest (the reply-action reads them too) — we
    // never switch them to an object or the existing readers break. `to`/`cc`
    // are always set (possibly empty so the FE can tell "no recipients" from
    // "not enriched"); `from` and `bcc` are written only when present —
    // inbound mail never carries a Bcc, and an unknown sender shouldn't render
    // a blank "From" row.
    private static void AddMailHeaders(
        Dictionary<string, JsonElement> dict,
        string? fromAddress, string? fromName,
        IReadOnlyList<MailRecipientRow> recipients)
    {
        if (!string.IsNullOrWhiteSpace(fromAddress))
        {
            dict["from"] = JsonSerializer.SerializeToElement(fromAddress);
            if (!string.IsNullOrWhiteSpace(fromName))
                dict["fromName"] = JsonSerializer.SerializeToElement(fromName);
        }

        dict["to"] = JsonSerializer.SerializeToElement(RecipientsOfKind(recipients, "to"));
        dict["cc"] = JsonSerializer.SerializeToElement(RecipientsOfKind(recipients, "cc"));

        var bcc = RecipientsOfKind(recipients, "bcc");
        if (bcc.Count > 0)
            dict["bcc"] = JsonSerializer.SerializeToElement(bcc);
    }

    private static List<object> RecipientsOfKind(
        IReadOnlyList<MailRecipientRow> recipients, string kind) =>
        recipients
            .Where(r => r.Kind == kind)
            .Select(r => (object)new { address = r.Address, name = r.DisplayName })
            .ToList();

    private static Dictionary<string, JsonElement> ParseMetadata(string metadataJson)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadataJson)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.Clone();
            }
        }
        catch { /* treat unparseable metadata as empty */ }
        return dict;
    }

    private static string RewriteCidReferences(
        string html, Guid ticketId, Guid mailId, IReadOnlyList<AttachmentRow> attachments,
        out int replaced, out int unmatched)
    {
        replaced = 0;
        unmatched = 0;
        // Build a case-insensitive lookup from Content-ID → Ready attachment id.
        // Graph returns ContentId without the surrounding angle brackets typical
        // in MIME headers, so we compare plain strings. Failed/Pending rows are
        // excluded — their cid: references get the placeholder below (and a
        // Pending one self-heals: enrichment runs at read time, so once the
        // attachment worker finishes, the next load rewrites the real URL).
        var byCid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attachments)
        {
            if (string.IsNullOrWhiteSpace(a.ContentId)) continue;
            if (a.ProcessingState != "Ready") continue;
            byCid[a.ContentId] = a.Id;
        }

        // Match `cid:<anything up to closing quote / whitespace / angle>`.
        // The replacement rebuilds an absolute path — the browser sends it
        // against the same origin, re-using the session cookie for auth.
        int r = 0, u = 0;
        var result = CidRegex.Replace(html, match =>
        {
            var raw = match.Groups[1].Value;
            var cid = raw.Trim().Trim('<', '>');
            if (byCid.TryGetValue(cid, out var attachmentId))
            {
                r++;
                return $"/api/tickets/{ticketId}/mail/{mailId}/attachments/{attachmentId}";
            }
            u++;
            // A cid the ingest has no attachment for (not carried by the
            // sender — common in forwarded/quoted threads — or not Ready
            // yet). Left as-is the browser shows a broken-image icon AND
            // logs a CSP violation per occurrence (cid: is not an allowed
            // img-src scheme); swap in a neutral inline placeholder
            // instead. Only when it is actually an image *source* — a
            // literal "cid:…" in visible mail text must stay text.
            return IsSrcAttributeValue(html, match.Index) ? CidPlaceholderDataUri : match.Value;
        });
        replaced = r;
        unmatched = u;
        return result;
    }

    /// True when the match at <paramref name="index"/> is the value of a
    /// src attribute (`src="cid:…`, `src='cid:…` or unquoted `src=cid:…`).
    private static bool IsSrcAttributeValue(string html, int index)
    {
        var i = index;
        if (i > 0 && (html[i - 1] == '"' || html[i - 1] == '\'')) i--;
        return i >= 4 && string.Compare(html, i - 4, "src=", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// Neutral "image unavailable" placeholder: a dashed frame with a small
    /// photo glyph, gray at ~50% so it reads on both themes. Served as a
    /// data: URI because the CSP allows `img-src data:` while the raw cid:
    /// scheme is blocked.
    private static readonly string CidPlaceholderDataUri =
        "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 96 72\">" +
            "<rect x=\"2\" y=\"2\" width=\"92\" height=\"68\" rx=\"8\" fill=\"gray\" fill-opacity=\".08\" " +
            "stroke=\"gray\" stroke-opacity=\".35\" stroke-width=\"2\" stroke-dasharray=\"5 5\"/>" +
            "<g transform=\"translate(37,25)\" fill=\"none\" stroke=\"gray\" stroke-opacity=\".6\" " +
            "stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            "<rect x=\"0\" y=\"0\" width=\"22\" height=\"22\" rx=\"3\"/>" +
            "<circle cx=\"6.5\" cy=\"6.5\" r=\"2\"/>" +
            "<path d=\"m1 17 5.5-5.5 4 4 5-5L21 16\"/></g></svg>"));

    private static Guid? TryGetMailMessageId(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("mail_message_id", out var prop)) return null;
            return prop.ValueKind switch
            {
                JsonValueKind.String when Guid.TryParse(prop.GetString(), out var g) => g,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex CidRegex = new(
        @"cid:([^\s""'<>)]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
