using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.IntakeForms;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
using Servicedesk.Infrastructure.TaggingMailboxes;
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
    private readonly ITaggingMailboxRepository _taggingMailboxes;
    private readonly IIntakeFormRepository _intakeForms;
    private readonly IIntakeFormTokenService _intakeTokens;
    private readonly ITriggerService _triggers;
    private readonly ISignatureComposer _signatures;
    private readonly IQueueAccessService _queueAccess;
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
        ITaggingMailboxRepository taggingMailboxes,
        IIntakeFormRepository intakeForms,
        IIntakeFormTokenService intakeTokens,
        ITriggerService triggers,
        ISignatureComposer signatures,
        IQueueAccessService queueAccess,
        ILogger<OutboundMailService> logger)
    {
        _queueAccess = queueAccess;
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
        _taggingMailboxes = taggingMailboxes;
        _intakeForms = intakeForms;
        _intakeTokens = intakeTokens;
        _triggers = triggers;
        _signatures = signatures;
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
        // our subject-based fallback both have a reliable marker. The visible
        // prefix is admin-configurable (Tickets.ReferencePrefix → "Ticket#"),
        // so the tag reads "[Ticket#1234]".
        var refPrefix = await _settings.GetAsync<string>(SettingKeys.Tickets.ReferencePrefix, ct);
        if (string.IsNullOrWhiteSpace(refPrefix)) refPrefix = TicketReference.DefaultPrefix;
        var subject = NormalizeSubject(request.Subject, detail.Ticket.Number, refPrefix);

        // Compose-template inline images (v0.0.92). A template body can carry
        // <img> tags pointing at the shared template-image endpoint
        // (/api/compose-templates/images/{id}) — an URL only agents can read.
        // Before the mail leaves, each referenced image is copied onto this
        // ticket as a staged attachment row (content-addressed blob store, so
        // no byte duplication) and the body is rewritten to the canonical
        // ticket-attachment URL. The regular inline pipeline below then
        // cid-embeds it exactly like a hand-pasted image, and the persisted
        // timeline body references the ticket-owned copy — deleting the
        // template or its image later can never break an already-sent mail.
        var effectiveBody = await MaterializeTemplateImagesAsync(request, ct);

        // Body-referenced attachment images (v0.1.6). The client's
        // attachmentIds list is only a hint: it is lost when a saved draft is
        // restored, and it never covers images the agent copied out of a
        // timeline article or another ticket's composer. The body itself is
        // the source of truth — every attachment URL in it is resolved here,
        // copied onto this ticket where needed (after an access check on the
        // source ticket), and rewritten to a canonical URL the inline
        // pipeline below understands. See MaterializeBodyAttachmentReferencesAsync.
        var bodyRefs = await MaterializeBodyAttachmentReferencesAsync(request, effectiveBody.BodyHtml, ct);
        effectiveBody = new MaterializedTemplateImages(
            bodyRefs.BodyHtml,
            effectiveBody.NewAttachmentIds.Concat(bodyRefs.AttachmentIds).ToList());

        var attachmentIds = new List<Guid>(request.AttachmentIds ?? Array.Empty<Guid>());
        attachmentIds.AddRange(effectiveBody.NewAttachmentIds);

        // Resolve attachments and prepare inline/cid-rewrite + Graph payload.
        // We accept any user-supplied id but only act on rows that *belong*
        // to this ticket and are still staged (owner_kind='Ticket', no
        // event_id) — everything else is silently dropped, treated identically
        // to "id not found".
        var preparedBody = effectiveBody.BodyHtml;
        var graphAttachments = new List<GraphOutboundAttachment>();
        var reassignments = new List<AttachmentReassignToMail>();
        if (attachmentIds.Count > 0)
        {
            var totalCap = await _settings.GetAsync<long>(SettingKeys.Mail.MaxOutboundTotalBytes, ct);
            if (totalCap <= 0) totalCap = 25 * 1024 * 1024;

            long running = 0;
            foreach (var attId in attachmentIds.Distinct())
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

                // Existence is pre-flighted here so a missing blob fails the
                // request before the Graph draft exists; the bytes themselves
                // are opened lazily inside GraphMailClient so a large part
                // streams straight from blob-storage into the upload session
                // instead of being buffered whole.
                var contentHash = row.ContentHash!;
                var attachmentId = row.Id;
                if (!await _blobs.ExistsAsync(contentHash, ct))
                    throw new InvalidOperationException(
                        $"Attachment {attachmentId} is Ready but blob {contentHash} is missing from storage.");

                graphAttachments.Add(new GraphOutboundAttachment(
                    FileName: row.OriginalFilename,
                    ContentType: row.MimeType,
                    SizeBytes: row.SizeBytes,
                    OpenContentAsync: async openCt => await _blobs.OpenReadAsync(contentHash, openCt)
                        ?? throw new InvalidOperationException(
                            $"Attachment {attachmentId} is Ready but blob {contentHash} is missing from storage."),
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

        // Email signature (v0.0.58; placement reworked v0.0.61). Applied only to
        // the wire body, never to the timeline event body (the cid images would
        // render broken in-app). A reply is anything sent into an existing
        // thread (anchor present); whether replies get a signature is
        // admin-configurable. A null result means "no signature applies".
        //
        // When the compose window pre-loaded the signature (SignaturePreloaded),
        // the body carries a <div data-sd-signature> marker directly under the
        // agent's message — above the quoted history. We swap that marker for
        // the authoritative cid render so the position the agent saw is what
        // goes out. If the agent removed the block, no marker remains and we add
        // nothing. Legacy clients (no preload) keep the append-at-the-bottom
        // behaviour.
        var signature = await _signatures.ComposeForQueueAsync(
            detail.Ticket.QueueId, request.AuthorUserId, isReply: anchor is not null, ct);
        if (request.SignaturePreloaded)
        {
            if (signature is not null && SignaturePlacement.HasMarker(preparedBody))
            {
                preparedBody = SignaturePlacement.ReplaceMarker(preparedBody, signature.Html);
                graphAttachments.AddRange(signature.Attachments);
            }
            else
            {
                // Agent deleted the block, or the signature is gated off now —
                // drop any stray marker so a bare <div> never goes on the wire.
                preparedBody = SignaturePlacement.StripMarker(preparedBody);
            }
        }
        else if (signature is not null)
        {
            preparedBody = AppendSignature(preparedBody, signature.Html);
            graphAttachments.AddRange(signature.Attachments);
        }
        else
        {
            preparedBody = SignaturePlacement.StripMarker(preparedBody);
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
        IReadOnlyList<Guid> mentionedMailboxIds = Array.Empty<Guid>();
        if (request.MentionedMailboxIds is { Count: > 0 } incomingMailboxes)
        {
            var resolvedMailboxes = await _taggingMailboxes.ResolveActiveByIdsAsync(incomingMailboxes, ct);
            mentionedMailboxIds = resolvedMailboxes.Select(m => m.Id).ToList();
        }

        // The timeline event keeps the agent's message + quote but never the
        // signature block: strip the marker so a bare <div data-sd-signature>
        // never lingers in the persisted body (the signature is wire-only).
        // Uses the template-image-rewritten body so the timeline references
        // the ticket-owned image copies, not the deletable template images.
        var persistedBodyHtml = SignaturePlacement.StripMarker(effectiveBody.BodyHtml);
        var bodyText = HtmlToText(persistedBodyHtml);
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
            mentionedMailboxIds = mentionedMailboxIds,
        });

        // Persist the user-readable body (with /api/.../attachments/{id} URLs,
        // not cid: refs) on the event so the timeline renders the inline images
        // via the same authenticated download endpoint as the editor used.
        var evt = await _tickets.AddEventAsync(request.TicketId, new NewTicketEvent(
            EventType: TicketEventType.MailSent.ToString(),
            BodyText: bodyText,
            BodyHtml: persistedBodyHtml,
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
        if (mentionedIds.Count > 0 || mentionedMailboxIds.Count > 0)
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
                BodyHtml: persistedBodyHtml,
                BodyText: bodyText,
                MentionedMailboxIds: mentionedMailboxIds), ct);
        }

        return OutboundMailResult.Ok(evt, mentionedIds.Count);
    }

    private static string NormalizeSubject(string subject, long ticketNumber, string prefix)
    {
        var clean = (subject ?? string.Empty).Trim();
        var tag = $"[{TicketReference.Format(ticketNumber, prefix)}]";
        // Already contains the CURRENT ticket's tag — typically on replies
        // where the customer's client carried our tag back in the Re:
        // subject. Don't double-tag. A stray tag from an unrelated thread
        // (forwarded conversation, copy/paste from another case) must not
        // block us from appending the current ticket's pointer.
        if (clean.Contains(tag, StringComparison.Ordinal)) return clean;
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

    private static string AppendSignature(string body, string signatureHtml)
    {
        if (string.IsNullOrWhiteSpace(signatureHtml)) return body;
        // A little vertical breathing room between the agent's text and the
        // signature block; the signature carries its own table layout.
        return $"{body}<br>{signatureHtml}";
    }

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
    // Compose-template inline images (v0.0.92) helpers
    // ============================================================

    private sealed record MaterializedTemplateImages(
        string BodyHtml,
        IReadOnlyList<Guid> NewAttachmentIds);

    private static readonly Regex ComposeTemplateImageUrlRegex = new(
        @"/api/compose-templates/images/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Copies every compose-template image referenced in the body onto the
    /// ticket as a staged attachment row (same content hash — the blob store
    /// is content-addressed, so only a metadata row is added) and rewrites
    /// the body to the canonical ticket-attachment URL the inline/cid
    /// pipeline understands. An id that doesn't resolve to a Ready
    /// template-image row is left untouched: the recipient gets a broken
    /// image instead of the whole send failing, and the warning below gives
    /// the admin the trail. Only 'ComposeTemplateImage' rows qualify —
    /// a guessed attachment id from another ticket can't be exfiltrated
    /// through this path.
    private async Task<MaterializedTemplateImages> MaterializeTemplateImagesAsync(
        OutboundMailRequest request, CancellationToken ct)
    {
        var body = request.BodyHtml;
        var imageIds = ExtractComposeTemplateImageIds(body);
        if (imageIds.Count == 0)
            return new MaterializedTemplateImages(body, Array.Empty<Guid>());

        var newIds = new List<Guid>(imageIds.Count);
        foreach (var imageId in imageIds)
        {
            var image = await _attachments.GetByIdAsync(imageId, ct);
            if (image is null
                || image.OwnerKind != "ComposeTemplateImage"
                || image.ProcessingState != "Ready"
                || string.IsNullOrWhiteSpace(image.ContentHash)
                || !image.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Compose-template image {ImageId} referenced in outbound mail for ticket {TicketId} did not resolve to a Ready template image; URL left as-is.",
                    imageId, request.TicketId);
                continue;
            }

            var copyId = await _attachments.CreateUploadedAsync(new NewUploadedAttachment(
                TicketId: request.TicketId,
                ContentHash: image.ContentHash!,
                SizeBytes: image.SizeBytes,
                MimeType: image.MimeType,
                OriginalFilename: image.OriginalFilename), ct);

            body = body.Replace(
                ComposeTemplateImageUrl(imageId),
                $"/api/tickets/{request.TicketId}/attachments/{copyId}",
                StringComparison.OrdinalIgnoreCase);
            newIds.Add(copyId);
        }

        return new MaterializedTemplateImages(body, newIds);
    }

    // ============================================================
    // Body-referenced attachment images (v0.1.6) helpers
    // ============================================================

    private sealed record MaterializedBodyReferences(
        string BodyHtml,
        IReadOnlyList<Guid> AttachmentIds);

    /// One parsed attachment URL from the body. <see cref="Raw"/> is the exact
    /// text as it appears (including any <c>?inline=true</c> suffix) so the
    /// rewrite can swap it verbatim.
    internal sealed record BodyAttachmentReference(
        string Raw,
        Guid TicketId,
        Guid? MailMessageId,
        Guid AttachmentId);

    /// Matches both attachment download routes the SPA renders images from:
    ///   /api/tickets/{ticketId}/attachments/{attachmentId}
    ///   /api/tickets/{ticketId}/mail/{mailMessageId}/attachments/{attachmentId}
    /// with an optional query string (the timeline appends ?inline=true when
    /// an agent opens an image in a new tab and copies it from there).
    private static readonly Regex BodyAttachmentUrlRegex = new(
        @"/api/tickets/(?<ticket>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})" +
        @"(?:/mail/(?<mail>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}))?" +
        @"/attachments/(?<att>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})" +
        @"(?:\?[^""'\s<>]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Distinct attachment URLs referenced anywhere in the body, in
    /// first-appearance order. Internal so tests can pin the URL contract
    /// shared by the download endpoints, the editor, and this rewrite.
    internal static IReadOnlyList<BodyAttachmentReference> ExtractBodyAttachmentReferences(string bodyHtml)
    {
        if (string.IsNullOrEmpty(bodyHtml)) return Array.Empty<BodyAttachmentReference>();
        var refs = new List<BodyAttachmentReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in BodyAttachmentUrlRegex.Matches(bodyHtml))
        {
            if (!seen.Add(m.Value)) continue;
            if (!Guid.TryParse(m.Groups["ticket"].Value, out var ticketId)) continue;
            if (!Guid.TryParse(m.Groups["att"].Value, out var attachmentId)) continue;
            Guid? mailId = null;
            if (m.Groups["mail"].Success && Guid.TryParse(m.Groups["mail"].Value, out var parsedMail))
                mailId = parsedMail;
            refs.Add(new BodyAttachmentReference(m.Value, ticketId, mailId, attachmentId));
        }
        return refs;
    }

    internal static string TicketAttachmentUrl(Guid ticketId, Guid attachmentId)
        => $"/api/tickets/{ticketId}/attachments/{attachmentId}";

    /// Resolves every attachment URL in the body to a row that is staged on
    /// *this* ticket, so the inline/cid pipeline can pick it up regardless of
    /// what the client put in <c>attachmentIds</c>. Three cases:
    ///
    ///  1. Already staged on this ticket (fresh upload, no event yet) — the id
    ///     is simply added to the send list. This is the draft-restore repair:
    ///     the client forgot the id, the body did not.
    ///  2. Bound to an earlier article on this ticket, or an inbound-mail
    ///     attachment of this ticket — a row already owned by an event can't
    ///     be re-bound to the new mail, so the bytes are re-staged as a new
    ///     row (content-addressed blob store: metadata only, no byte copy).
    ///  3. On another ticket — same ownership chain the download endpoints
    ///     enforce (attachment → mail → ticket, or attachment → event →
    ///     ticket) PLUS a queue-access check for the sender on the source
    ///     ticket. Without access the URL is left untouched (the recipient
    ///     gets a broken image, exactly as before) — never a copy. This is the
    ///     exfiltration guard: a guessed URL of a restricted queue's image
    ///     must not become a readable inline part.
    ///
    /// Only <c>image/*</c> rows qualify; non-image URLs are left as-is (the
    /// inline pipeline never cid-embeds them either). Every rewritten URL is
    /// normalised to the canonical form (query string dropped) so a
    /// <c>?inline=true</c> suffix can't leak into the <c>cid:</c> reference.
    private async Task<MaterializedBodyReferences> MaterializeBodyAttachmentReferencesAsync(
        OutboundMailRequest request, string body, CancellationToken ct)
    {
        var refs = ExtractBodyAttachmentReferences(body);
        if (refs.Count == 0)
            return new MaterializedBodyReferences(body, Array.Empty<Guid>());

        var ids = new List<Guid>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Per-send memo so the same image referenced twice (quote + body)
        // resolves once and maps onto one copy.
        var resolvedCopies = new Dictionary<Guid, Guid>();
        var accessByTicket = new Dictionary<Guid, bool>();

        foreach (var r in refs)
        {
            if (resolvedCopies.TryGetValue(r.AttachmentId, out var existingCopy))
            {
                replacements[r.Raw] = TicketAttachmentUrl(request.TicketId, existingCopy);
                continue;
            }

            var row = await _attachments.GetByIdAsync(r.AttachmentId, ct);
            if (row is null
                || row.ProcessingState != "Ready"
                || string.IsNullOrWhiteSpace(row.ContentHash)
                || !row.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Case 1 — staged on this ticket: nothing to copy, just make sure
            // the inline pipeline sees it and the URL is canonical.
            if (r.MailMessageId is null
                && r.TicketId == request.TicketId
                && row.OwnerKind == "Ticket"
                && row.OwnerId == request.TicketId
                && row.EventId is null)
            {
                if (!ids.Contains(row.Id)) ids.Add(row.Id);
                resolvedCopies[row.Id] = row.Id;
                replacements[r.Raw] = TicketAttachmentUrl(request.TicketId, row.Id);
                continue;
            }

            // Ownership chain, identical to the download endpoints. Any break
            // means the URL is not a legitimate reference and stays untouched.
            bool chainOk;
            if (r.MailMessageId is { } mailId)
            {
                if (row.OwnerKind != "Mail" || row.OwnerId != mailId) continue;
                var mailRow = await _mail.GetByIdAsync(mailId, ct);
                chainOk = mailRow is not null && mailRow.TicketId == r.TicketId;
            }
            else
            {
                var ownsDirect = row.OwnerKind == "Ticket" && row.OwnerId == r.TicketId && row.EventId is null;
                var ownsViaEvent = row.EventId.HasValue
                    && await _tickets.EventBelongsToTicketAsync(r.TicketId, row.EventId.Value, ct);
                chainOk = ownsDirect || ownsViaEvent;
            }
            if (!chainOk) continue;

            // Case 3 — another ticket: the sender must be allowed to read it.
            if (r.TicketId != request.TicketId)
            {
                if (!accessByTicket.TryGetValue(r.TicketId, out var allowed))
                {
                    allowed = false;
                    if (!string.IsNullOrWhiteSpace(request.AuthorRole))
                    {
                        var source = await _tickets.GetByIdAsync(r.TicketId, ct);
                        if (source is not null)
                        {
                            allowed = await _queueAccess.HasQueueAccessAsync(
                                request.AuthorUserId, request.AuthorRole, source.Ticket.QueueId, ct);
                        }
                    }
                    accessByTicket[r.TicketId] = allowed;
                }
                if (!allowed)
                {
                    _logger.LogWarning(
                        "Outbound mail on ticket {TicketId} references attachment {AttachmentId} of ticket {SourceTicketId}, which user {UserId} cannot access; URL left as-is.",
                        request.TicketId, r.AttachmentId, r.TicketId, request.AuthorUserId);
                    continue;
                }
            }

            // Case 2/3 — re-stage the bytes on this ticket. Content-addressed
            // store: a metadata row only.
            var copyId = await _attachments.CreateUploadedAsync(new NewUploadedAttachment(
                TicketId: request.TicketId,
                ContentHash: row.ContentHash!,
                SizeBytes: row.SizeBytes,
                MimeType: row.MimeType,
                OriginalFilename: row.OriginalFilename), ct);
            ids.Add(copyId);
            resolvedCopies[row.Id] = copyId;
            replacements[r.Raw] = TicketAttachmentUrl(request.TicketId, copyId);
        }

        if (replacements.Count == 0)
            return new MaterializedBodyReferences(body, ids);

        var rewritten = BodyAttachmentUrlRegex.Replace(body,
            m => replacements.TryGetValue(m.Value, out var replacement) ? replacement : m.Value);
        return new MaterializedBodyReferences(rewritten, ids);
    }

    /// Distinct compose-template image ids referenced anywhere in the body,
    /// in first-appearance order. Internal so tests can pin the URL contract
    /// shared by the upload endpoint, the frontend editor, and this rewrite.
    internal static IReadOnlyList<Guid> ExtractComposeTemplateImageIds(string bodyHtml)
    {
        if (string.IsNullOrEmpty(bodyHtml)) return Array.Empty<Guid>();
        var ids = new List<Guid>();
        foreach (Match m in ComposeTemplateImageUrlRegex.Matches(bodyHtml))
        {
            if (Guid.TryParse(m.Groups[1].Value, out var id) && !ids.Contains(id))
                ids.Add(id);
        }
        return ids;
    }

    /// The canonical URL the upload endpoint returns and the template editor
    /// embeds — the single string both sides of the rewrite agree on.
    internal static string ComposeTemplateImageUrl(Guid imageId)
        => $"/api/compose-templates/images/{imageId}";

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
