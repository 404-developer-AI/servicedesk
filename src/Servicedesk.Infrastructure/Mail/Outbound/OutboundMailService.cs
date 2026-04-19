using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Mail.Outbound;

public sealed class OutboundMailService : IOutboundMailService
{
    private static readonly Regex TagTrim = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceTrim = new(@"\s+", RegexOptions.Compiled);

    private readonly IGraphMailClient _graph;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ITicketRepository _tickets;
    private readonly IMailMessageRepository _mail;
    private readonly IAttachmentRepository _attachments;
    private readonly IBlobStore _blobs;
    private readonly ISettingsService _settings;
    private readonly ISlaEngine _sla;
    private readonly IUserService _users;
    private readonly IMentionNotificationService _mentions;
    private readonly ILogger<OutboundMailService> _logger;

    public OutboundMailService(
        IGraphMailClient graph,
        ITaxonomyRepository taxonomy,
        ITicketRepository tickets,
        IMailMessageRepository mail,
        IAttachmentRepository attachments,
        IBlobStore blobs,
        ISettingsService settings,
        ISlaEngine sla,
        IUserService users,
        IMentionNotificationService mentions,
        ILogger<OutboundMailService> logger)
    {
        _graph = graph;
        _taxonomy = taxonomy;
        _tickets = tickets;
        _mail = mail;
        _attachments = attachments;
        _blobs = blobs;
        _settings = settings;
        _sla = sla;
        _users = users;
        _mentions = mentions;
        _logger = logger;
    }

    public async Task<OutboundMailResult> SendAsync(OutboundMailRequest request, CancellationToken ct)
    {
        if (request.To.Count == 0 && request.Cc.Count == 0 && request.Bcc.Count == 0)
            return OutboundMailResult.Invalid("At least one recipient is required.");
        if (string.IsNullOrWhiteSpace(request.Subject))
            return OutboundMailResult.Invalid("Subject is required.");
        if (string.IsNullOrWhiteSpace(request.BodyHtml))
            return OutboundMailResult.Invalid("Body is required.");

        var detail = await _tickets.GetByIdAsync(request.TicketId, ct);
        if (detail is null) return OutboundMailResult.NotFound();

        var queue = await _taxonomy.GetQueueAsync(detail.Ticket.QueueId, ct);
        var fromMailbox = FirstNonEmpty(queue?.OutboundMailboxAddress, queue?.InboundMailboxAddress);
        if (string.IsNullOrWhiteSpace(fromMailbox))
            return OutboundMailResult.MissingMailbox();

        // Plus-addressing is our inbound-threading backstop: even if the recipient's
        // mail client drops In-Reply-To / References, a reply sent to the plus-address
        // still routes back to this ticket via MailIngestService.ResolveExistingTicket.
        var plusToken = await _settings.GetAsync<string>(SettingKeys.Mail.PlusAddressToken, ct);
        if (string.IsNullOrWhiteSpace(plusToken)) plusToken = "TCK";
        var replyToAddress = BuildPlusAddress(fromMailbox, plusToken, detail.Ticket.Number);
        var fromName = !string.IsNullOrWhiteSpace(queue?.Name) ? queue!.Name : fromMailbox;

        var anchor = await _mail.GetLatestThreadAnchorAsync(request.TicketId, ct);

        // Always ensure the ticket tag is in the subject so the customer sees
        // which ticket this is about — and so their client-side threading plus
        // our subject-based fallback both have a reliable marker.
        var subject = NormalizeSubject(request.Subject, detail.Ticket.Number);

        // Resolve attachments and prepare inline/cid-rewrite + Graph payload.
        // We accept any user-supplied id but only act on rows that *belong*
        // to this ticket and are still staged (owner_kind='Ticket', no
        // event_id) — everything else is silently dropped, treated identically
        // to "id not found".
        var preparedBody = request.BodyHtml;
        var graphAttachments = new List<GraphOutboundAttachment>();
        var reassignments = new List<AttachmentReassignToMail>();
        if (request.AttachmentIds is { Count: > 0 } incomingIds)
        {
            var totalCap = await _settings.GetAsync<long>(SettingKeys.Mail.MaxOutboundTotalBytes, ct);
            if (totalCap <= 0) totalCap = 3 * 1024 * 1024;

            long running = 0;
            foreach (var attId in incomingIds.Distinct())
            {
                var row = await _attachments.GetByIdAsync(attId, ct);
                if (row is null) continue;
                if (row.OwnerKind != "Ticket" || row.OwnerId != request.TicketId) continue;
                if (row.EventId is not null) continue;
                if (row.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(row.ContentHash)) continue;

                running += row.SizeBytes;
                if (running > totalCap)
                {
                    return OutboundMailResult.TooLarge(
                        $"Attachments exceed the {Math.Max(1, totalCap / (1024 * 1024))} MB outbound mail limit (Mail.MaxOutboundTotalBytes). " +
                        "Split this mail into multiple sends, or share the file via a separate link.");
                }

                // Decide inline vs attached by scanning the body for the
                // attachment's URL. The frontend injects a single canonical
                // URL per inline image (`/api/tickets/{ticketId}/attachments/{id}`),
                // so a literal substring match is enough — no HTML parsing
                // required. Non-image MIME types are never treated as inline
                // even if the URL appears in the body, because cid-embed of
                // a PDF would render badly in most clients.
                var canonicalUrl = $"/api/tickets/{request.TicketId}/attachments/{row.Id}";
                var inDocument = preparedBody.Contains(canonicalUrl, StringComparison.OrdinalIgnoreCase);
                var imagesh = row.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                var isInline = inDocument && imagesh;

                string? contentId = null;
                if (isInline)
                {
                    // Synthetic Content-Id local-part using the attachment id:
                    // unique within the message, opaque to the recipient, and
                    // recoverable later if we ever need to reverse the link.
                    contentId = $"sd-{row.Id:N}@servicedesk.local";
                    preparedBody = ReplaceAttachmentUrlWithCid(preparedBody, canonicalUrl, contentId);
                }

                await using var blobStream = await _blobs.OpenReadAsync(row.ContentHash!, ct)
                    ?? throw new InvalidOperationException(
                        $"Attachment {row.Id} is Ready but blob {row.ContentHash} is missing from storage.");
                using var ms = new MemoryStream();
                await blobStream.CopyToAsync(ms, ct);

                graphAttachments.Add(new GraphOutboundAttachment(
                    FileName: row.OriginalFilename,
                    ContentType: row.MimeType,
                    Bytes: ms.ToArray(),
                    IsInline: isInline,
                    ContentId: contentId));

                reassignments.Add(new AttachmentReassignToMail(
                    AttachmentId: row.Id,
                    ContentId: contentId,
                    IsInline: isInline));
            }
        }

        var sendResult = await _graph.SendMailAsync(new GraphOutboundMessage(
            FromMailbox: fromMailbox,
            Subject: subject,
            BodyHtml: preparedBody,
            To: request.To,
            Cc: request.Cc,
            Bcc: request.Bcc,
            ReplyTo: new[] { new GraphRecipient(replyToAddress, fromName) },
            Attachments: graphAttachments.Count > 0 ? graphAttachments : null), ct);

        // @@-mention filtering (v0.0.12 stap 3): dropped same way as the events
        // path — unknown ids / customer ids / deleted-user ids silently vanish.
        IReadOnlyList<Guid> mentionedIds = Array.Empty<Guid>();
        if (request.MentionedUserIds is { Count: > 0 } incomingMentions)
        {
            mentionedIds = await _users.FilterAgentIdsAsync(incomingMentions, ct);
        }

        var bodyText = HtmlToText(request.BodyHtml);
        var metadata = JsonSerializer.Serialize(new
        {
            kind = request.Kind.ToString(),
            from = fromMailbox,
            fromName,
            replyTo = replyToAddress,
            subject,
            to = request.To.Select(r => new { address = r.Address, name = r.Name }),
            cc = request.Cc.Select(r => new { address = r.Address, name = r.Name }),
            bcc = request.Bcc.Select(r => new { address = r.Address, name = r.Name }),
            internet_message_id = sendResult.InternetMessageId,
            in_reply_to = anchor?.MessageId,
            mentionedUserIds = mentionedIds,
        });

        // Persist the user-readable body (with /api/.../attachments/{id} URLs,
        // not cid: refs) on the event so the timeline renders the inline images
        // via the same authenticated download endpoint as the editor used.
        var evt = await _tickets.AddEventAsync(request.TicketId, new NewTicketEvent(
            EventType: TicketEventType.MailSent.ToString(),
            BodyText: bodyText,
            BodyHtml: request.BodyHtml,
            IsInternal: false,
            AuthorUserId: request.AuthorUserId,
            MetadataJson: metadata), ct);
        if (evt is null)
        {
            // Ticket disappeared between the SendMail call and AddEvent. The mail
            // has already left the mailbox — log it so an admin can reconcile,
            // then report NotFound. We intentionally don't insert a mail_messages
            // row because there's no ticket to attach it to.
            _logger.LogWarning(
                "Outbound mail sent from {Mailbox} (internet-message-id {MsgId}) but ticket {TicketId} was not found for event append.",
                fromMailbox, sendResult.InternetMessageId, request.TicketId);
            return OutboundMailResult.NotFound();
        }

        var recipients = new List<NewMailRecipient>(request.To.Count + request.Cc.Count + request.Bcc.Count);
        foreach (var r in request.To) recipients.Add(new NewMailRecipient("to", r.Address, r.Name));
        foreach (var r in request.Cc) recipients.Add(new NewMailRecipient("cc", r.Address, r.Name));
        foreach (var r in request.Bcc) recipients.Add(new NewMailRecipient("bcc", r.Address, r.Name));

        var mailMessageId = await _mail.InsertOutboundAsync(new NewOutboundMailMessage(
            MessageId: sendResult.InternetMessageId,
            InReplyTo: anchor?.MessageId,
            References: ComposeReferences(anchor),
            Subject: subject,
            FromAddress: fromMailbox,
            FromName: fromName,
            MailboxAddress: fromMailbox,
            SentUtc: sendResult.SentUtc.UtcDateTime,
            BodyText: bodyText,
            TicketId: request.TicketId,
            TicketEventId: evt.Id), recipients, ct);

        // Move the staged attachment rows onto the mail-message so the
        // timeline-enricher's existing ListByMailAsync path picks them up
        // exactly the way it does for inbound mail. Inline rows now carry
        // the synthetic content_id we generated above. After this update
        // the rows are no longer staged-on-ticket — neither another outbound
        // mail nor a Note can re-claim them.
        if (reassignments.Count > 0)
        {
            await _attachments.ReassignToMailAsync(reassignments, request.TicketId, mailMessageId, evt.Id, ct);
        }

        await _sla.OnTicketEventAsync(request.TicketId, evt.EventType, ct);

        // @@-mention notification raamwerk (v0.0.12 stap 4). Mirrors the
        // event-endpoint hook; fire-and-forget so a notification-channel
        // failure never undoes the mail we already put on the wire.
        if (mentionedIds.Count > 0)
        {
            var sourceUser = await _users.FindByIdAsync(request.AuthorUserId, ct);
            await _mentions.PublishAsync(new MentionNotificationSource(
                TicketId: request.TicketId,
                TicketNumber: detail.Ticket.Number,
                TicketSubject: detail.Ticket.Subject,
                QueueId: detail.Ticket.QueueId,
                EventId: evt.Id,
                EventType: evt.EventType,
                SourceUserId: request.AuthorUserId,
                SourceUserEmail: sourceUser?.Email ?? string.Empty,
                MentionedUserIds: mentionedIds,
                BodyHtml: request.BodyHtml,
                BodyText: bodyText), ct);
        }

        return OutboundMailResult.Ok(evt, mentionedIds.Count);
    }

    private static readonly Regex AnyTicketTag = new(@"\[#\d+\]", RegexOptions.Compiled);

    private static string NormalizeSubject(string subject, long ticketNumber)
    {
        var clean = (subject ?? string.Empty).Trim();
        var tag = $"[#{ticketNumber}]";
        // Already contains *any* ticket-tag — typically on replies where the
        // customer's client carried our tag back in the Re: subject. Don't
        // double-tag; don't rewrite an existing tag either (e.g. a merged
        // conversation that still references the original ticket).
        if (AnyTicketTag.IsMatch(clean)) return clean;
        return string.IsNullOrEmpty(clean) ? tag : $"{clean} {tag}";
    }

    private static string BuildPlusAddress(string mailbox, string token, long ticketNumber)
    {
        var at = mailbox.IndexOf('@');
        if (at <= 0 || at == mailbox.Length - 1) return mailbox;
        var local = mailbox[..at];
        var domain = mailbox[(at + 1)..];
        return $"{local}+{token}-{ticketNumber}@{domain}";
    }

    private static string? ComposeReferences(MailThreadAnchor? anchor)
    {
        if (anchor is null) return null;
        var parent = $"<{anchor.MessageId}>";
        return string.IsNullOrWhiteSpace(anchor.References) ? parent : $"{anchor.References} {parent}";
    }

    private static string? FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var stripped = TagTrim.Replace(html, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return WhitespaceTrim.Replace(decoded, " ").Trim();
    }

    /// Replace every occurrence of the canonical attachment URL inside an
    /// <c>img src</c> (or any other quoted attribute) with the inline cid.
    /// Avoids any HTML parser; the URL is opaque and contains no regex
    /// metacharacters that would survive Regex.Escape.
    private static string ReplaceAttachmentUrlWithCid(string body, string url, string contentId)
    {
        return body.Replace(url, $"cid:{contentId}", StringComparison.OrdinalIgnoreCase);
    }
}
