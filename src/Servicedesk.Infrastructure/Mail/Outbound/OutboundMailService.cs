using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.IntakeForms;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
using Servicedesk.Infrastructure.Triggers;

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
    private readonly IIntakeFormRepository _intakeForms;
    private readonly IIntakeFormTokenService _intakeTokens;
    private readonly ITriggerService _triggers;
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
        IIntakeFormRepository intakeForms,
        IIntakeFormTokenService intakeTokens,
        ITriggerService triggers,
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
        _intakeForms = intakeForms;
        _intakeTokens = intakeTokens;
        _triggers = triggers;
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

        // Intake Forms (v0.0.19). For each LinkedFormIds entry we mint a
        // token + hash + cipher up-front so the link embedded in the mail
        // body matches the server-side lookup key exactly. The atomic
        // state-flip (Draft → Sent + IntakeFormSent event) happens AFTER
        // Graph accepts the message so a delivery failure doesn't leave a
        // Sent instance whose link never reached the customer.
        var intakePrep = await PrepareIntakeFormsAsync(request, preparedBody, ct);
        preparedBody = intakePrep.BodyHtml;

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

        // Trigger evaluator (v0.0.24 follow-up). The MailSent event we
        // just wrote is an article-added signal; without this hook
        // action-triggers conditioned on `article.type = MailSent`
        // (typical "agent replied → set status to WFC" automation)
        // would never fire. Mirrors the AddTicketEvent and MailIngest
        // hook-points; the trigger evaluator's own send_mail path is
        // separate and never re-enters here.
        await _triggers.EvaluateAsync(
            ticketId: request.TicketId,
            ticketEventId: evt.Id,
            activatorKind: TriggerActivatorKind.Action,
            changeSet: TriggerChangeSet.ArticleOnly(),
            ct: ct);

        // Intake Forms finalize step. Atomic Draft → Sent + IntakeFormSent
        // event per prepared instance. A rare failure here (form cancelled
        // between mint and send) only logs — the mail is already out the
        // door with a link whose hash is absent from the DB, so the
        // customer will see a 404 on click. Admin can re-send manually.
        if (intakePrep.Prepared.Count > 0)
        {
            await FinalizeIntakeFormsAsync(request, intakePrep.Prepared, ct);
        }

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

    // ============================================================
    // Intake Forms (v0.0.19) helpers
    // ============================================================

    private sealed record PreparedIntakeForm(
        Guid InstanceId,
        string TemplateName,
        string RawToken,
        byte[] TokenHash,
        byte[] TokenCipher,
        DateTime ExpiresUtc,
        string SentToEmail);

    private sealed record IntakePreparationResult(
        string BodyHtml,
        IReadOnlyList<PreparedIntakeForm> Prepared);

    /// Mints tokens, swaps the `::`-mention chip placeholder in the body
    /// for a real anchor, and returns the list of prepared forms. The
    /// state-flip to Sent happens later (post-send) so a failed delivery
    /// doesn't leave orphan Sent rows.
    private async Task<IntakePreparationResult> PrepareIntakeFormsAsync(
        OutboundMailRequest request, string bodyHtml, CancellationToken ct)
    {
        if (request.LinkedFormIds is not { Count: > 0 } ids)
            return new IntakePreparationResult(bodyHtml, Array.Empty<PreparedIntakeForm>());

        var baseUrl = (await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct))?.TrimEnd('/') ?? string.Empty;
        var expiryDays = Math.Max(1, await _settings.GetAsync<int>(SettingKeys.IntakeForms.DefaultExpiryDays, ct));
        var now = DateTime.UtcNow;
        var expiresUtc = now.AddDays(expiryDays);

        // "To" first. If no primary recipient (CC/BCC-only send), fall
        // back to the requester email so we still have an audit trail of
        // which address this link went to. Empty → empty string; UI
        // doesn't require a recipient to be present.
        var sentToEmail = request.To.Count > 0 ? request.To[0].Address : string.Empty;

        var prepared = new List<PreparedIntakeForm>(ids.Count);
        var mutated = bodyHtml;

        foreach (var instanceId in ids.Distinct())
        {
            var view = await _intakeForms.GetAgentViewAsync(request.TicketId, instanceId, ct);
            if (view is null)
            {
                _logger.LogWarning(
                    "LinkedFormId {InstanceId} does not belong to ticket {TicketId}; dropping from outbound mail.",
                    instanceId, request.TicketId);
                continue;
            }
            if (view.Instance.Status != IntakeFormStatus.Draft)
            {
                _logger.LogWarning(
                    "LinkedFormId {InstanceId} is not in Draft state (current={Status}); dropping.",
                    instanceId, view.Instance.Status);
                continue;
            }

            var (raw, hash, cipher) = _intakeTokens.Mint();
            var templateName = view.Template.Name;
            prepared.Add(new PreparedIntakeForm(instanceId, templateName, raw, hash, cipher, expiresUtc, sentToEmail));

            mutated = EmbedIntakeLink(mutated, instanceId, raw, templateName, baseUrl);
        }

        return new IntakePreparationResult(mutated, prepared);
    }

    /// Replace the Tiptap-emitted placeholder `<span data-intake-form="{id}">…</span>`
    /// with an anchor to the public form URL. If the placeholder is
    /// absent (agent linked forms without inline mention) we append a
    /// paragraph at the end — still delivers the link, doesn't alter the
    /// agent's carefully-crafted body.
    private static string EmbedIntakeLink(string bodyHtml, Guid instanceId, string rawToken, string templateName, string baseUrl)
    {
        var link = BuildIntakeUrl(baseUrl, rawToken);
        var encodedName = System.Net.WebUtility.HtmlEncode(templateName);
        var anchor = $"<a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">{encodedName}</a>";

        // Match either a self-closing or wrapping span that the Tiptap
        // intake-mention extension emits. We look for the data-attribute
        // specifically so we never touch unrelated markup.
        var marker = $"data-intake-form=\"{instanceId}\"";
        var markerIdx = bodyHtml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx >= 0)
        {
            // Walk back to the opening '<' of the containing element.
            var openIdx = bodyHtml.LastIndexOf('<', markerIdx);
            if (openIdx >= 0)
            {
                // Walk forward to the matching close '>'.
                var tagEndIdx = bodyHtml.IndexOf('>', markerIdx);
                if (tagEndIdx > openIdx)
                {
                    // Self-closing? (ends with "/>")
                    var isSelfClose = tagEndIdx > 0 && bodyHtml[tagEndIdx - 1] == '/';
                    if (isSelfClose)
                        return bodyHtml[..openIdx] + anchor + bodyHtml[(tagEndIdx + 1)..];

                    // Find the matching </span> (shallow — nested mentions
                    // aren't emitted by the editor).
                    var closeIdx = bodyHtml.IndexOf("</span>", tagEndIdx + 1, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx > tagEndIdx)
                        return bodyHtml[..openIdx] + anchor + bodyHtml[(closeIdx + "</span>".Length)..];
                }
            }
        }

        // No placeholder found → append a paragraph. Empty body → emit
        // a clean paragraph. Non-empty body → separate with a new line.
        var suffix = $"<p>{anchor}</p>";
        return string.IsNullOrWhiteSpace(bodyHtml) ? suffix : bodyHtml + suffix;
    }

    private static string BuildIntakeUrl(string baseUrl, string rawToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return $"/intake/{rawToken}";
        return $"{baseUrl}/intake/{rawToken}";
    }

    private async Task FinalizeIntakeFormsAsync(
        OutboundMailRequest request, IReadOnlyList<PreparedIntakeForm> prepared, CancellationToken ct)
    {
        foreach (var p in prepared)
        {
            var metadata = JsonSerializer.Serialize(new
            {
                instanceId = p.InstanceId,
                templateName = p.TemplateName,
                expiresUtc = p.ExpiresUtc,
                sentToEmail = p.SentToEmail,
            });

            var sentEventId = await _intakeForms.SendDraftAsync(
                p.InstanceId, request.TicketId, request.AuthorUserId,
                p.TokenHash, p.TokenCipher, p.ExpiresUtc, p.SentToEmail,
                metadata, ct);

            if (sentEventId is null)
            {
                _logger.LogWarning(
                    "Intake form {InstanceId} on ticket {TicketId} could not be finalised — the Draft vanished between token mint and mail send. Link in the delivered mail will 404.",
                    p.InstanceId, request.TicketId);
            }
        }
    }
}
