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
    private readonly IMailMessageRepository _mail;
    private readonly IAttachmentRepository _attachments;
    private readonly IBlobStore _blobs;
    private readonly ILogger<MailTimelineEnricher> _logger;

    public MailTimelineEnricher(
        IMailMessageRepository mail,
        IAttachmentRepository attachments,
        IBlobStore blobs,
        ILogger<MailTimelineEnricher> logger)
    {
        _mail = mail;
        _attachments = attachments;
        _blobs = blobs;
        _logger = logger;
    }

    public async Task<TicketDetail> EnrichAsync(TicketDetail detail, CancellationToken ct)
    {
        if (detail.Events.Count == 0) return detail;

        var enriched = new List<TicketEvent>(detail.Events.Count);
        foreach (var evt in detail.Events)
        {
            if (evt.EventType != "MailReceived")
            {
                enriched.Add(evt);
                continue;
            }

            var mailId = TryGetMailMessageId(evt.MetadataJson);
            if (mailId is null)
            {
                enriched.Add(evt);
                continue;
            }

            var enrichedEvt = await TryEnrichAsync(detail.Ticket.Id, mailId.Value, evt, ct);
            enriched.Add(enrichedEvt);
        }

        return detail with { Events = enriched };
    }

    private async Task<TicketEvent> TryEnrichAsync(
        Guid ticketId, Guid mailId, TicketEvent evt, CancellationToken ct)
    {
        try
        {
            var attachments = await _attachments.ListByMailAsync(mailId, ct);
            var readyCount = attachments.Count(a => a.ProcessingState == "Ready");
            var pendingCount = attachments.Count(a => a.ProcessingState == "Pending");
            var failedCount = attachments.Count(a => a.ProcessingState == "Failed");

            string? rewrittenHtml = null;
            int cidReplaced = 0, cidUnmatched = 0;
            var mail = await _mail.GetByIdAsync(mailId, ct);
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

            _logger.LogInformation(
                "[MailEnrich] ticket={TicketId} mail={MailId} attachments total={Total} ready={Ready} pending={Pending} failed={Failed} cid replaced={Replaced} unmatched={Unmatched}",
                ticketId, mailId, attachments.Count, readyCount, pendingCount, failedCount, cidReplaced, cidUnmatched);

            var newMetadata = InjectAttachments(evt.MetadataJson, ticketId, mailId, attachments);
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

    private static string InjectAttachments(
        string metadataJson, Guid ticketId, Guid mailId, IReadOnlyList<AttachmentRow> attachments)
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

        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
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
        }

        var attachmentsJson = JsonSerializer.SerializeToElement(items);
        dict["attachments"] = attachmentsJson;
        return JsonSerializer.Serialize(dict);
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
        // excluded — their cid: references stay as-is and simply won't render.
        var byCid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attachments)
        {
            if (string.IsNullOrWhiteSpace(a.ContentId)) continue;
            if (a.ProcessingState != "Ready") continue;
            byCid[a.ContentId] = a.Id;
        }
        if (byCid.Count == 0) return html;

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
            return match.Value;
        });
        replaced = r;
        unmatched = u;
        return result;
    }

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
