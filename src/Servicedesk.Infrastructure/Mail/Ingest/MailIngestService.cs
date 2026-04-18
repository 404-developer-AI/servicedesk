using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Mail.Ingest;

/// Pure orchestration: fetch → skip-checks → threading → persist → append event.
/// Attachment handling is deferred to a background worker (stap 6b); this
/// service only persists the mail row and raw .eml blob. All dependencies are
/// interfaces so the service is unit-testable without Postgres or Graph.
public sealed class MailIngestService : IMailIngestService
{
    private static readonly Regex TagTrim = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceTrim = new(@"\s+", RegexOptions.Compiled);

    private readonly IGraphMailClient _graph;
    private readonly IMailMessageRepository _mail;
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IContactLookupService _contacts;
    private readonly IBlobStore _blobs;
    private readonly ISettingsService _settings;
    private readonly ISlaEngine _sla;
    private readonly ILogger<MailIngestService> _logger;

    public MailIngestService(
        IGraphMailClient graph,
        IMailMessageRepository mail,
        ITicketRepository tickets,
        ITaxonomyRepository taxonomy,
        IContactLookupService contacts,
        IBlobStore blobs,
        ISettingsService settings,
        ISlaEngine sla,
        ILogger<MailIngestService> logger)
    {
        _graph = graph;
        _mail = mail;
        _tickets = tickets;
        _taxonomy = taxonomy;
        _contacts = contacts;
        _blobs = blobs;
        _settings = settings;
        _sla = sla;
        _logger = logger;
    }

    public async Task<MailIngestResult> IngestAsync(
        Guid queueId, string queueMailbox, string graphMessageId, CancellationToken ct)
    {
        GraphFullMessage msg;
        try
        {
            msg = await _graph.FetchMessageAsync(queueMailbox, graphMessageId, ct);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
            when (ex.ResponseStatusCode == 404
                || string.Equals(ex.Error?.Code, "ErrorItemNotFound", StringComparison.OrdinalIgnoreCase)
                || (ex.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // Message disappeared between delta-list and fetch — user deleted
            // it, we moved it to Processed in a previous cycle, etc. Not an
            // error: the delta cursor has already advanced past it.
            return new MailIngestResult(MailIngestOutcome.SkippedNotFound, null, null, null,
                "Graph returned not-found for this message id");
        }

        // Auto-Submitted header — skip anything except "no" per RFC 3834.
        if (!string.IsNullOrWhiteSpace(msg.AutoSubmitted)
            && !string.Equals(msg.AutoSubmitted.Trim(), "no", StringComparison.OrdinalIgnoreCase))
        {
            return new MailIngestResult(MailIngestOutcome.SkippedAutoSubmitted, null, null, null,
                $"Auto-Submitted: {msg.AutoSubmitted}");
        }

        // Loop prevention: ignore mail claiming to come from any of our own mailboxes.
        var queues = await _taxonomy.ListQueuesAsync(ct);
        var ownMailboxes = queues
            .SelectMany(q => new[] { q.InboundMailboxAddress, q.OutboundMailboxAddress })
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim().ToLowerInvariant())
            .ToHashSet();
        if (ownMailboxes.Contains(msg.From.Address.Trim().ToLowerInvariant()))
        {
            return new MailIngestResult(MailIngestOutcome.SkippedOwnMailbox, null, null, null,
                $"From={msg.From.Address} matches an own mailbox");
        }

        // Dedup: already seen this Message-ID?
        if (!string.IsNullOrWhiteSpace(msg.InternetMessageId))
        {
            var existing = await _mail.GetByMessageIdAsync(msg.InternetMessageId, ct);
            if (existing is not null)
            {
                return new MailIngestResult(MailIngestOutcome.Deduplicated,
                    existing.Id, existing.TicketId, existing.TicketEventId, "Message-ID already ingested");
            }
        }

        // Threading: plus-address → In-Reply-To/References → subject-token → new.
        var token = await _settings.GetAsync<string>(SettingKeys.Mail.PlusAddressToken, ct) ?? "TCK";
        var existingTicketId = await ResolveExistingTicketAsync(msg, token, ct);

        var bodyText = ExtractBodyText(msg);
        var snippet = Snippet(bodyText, 200);

        // Persist raw .eml + body_html blobs (best-effort — ingest proceeds
        // even if blob write fails so the mail doesn't get stuck on storage).
        string? rawHash = await TryStoreRawAsync(queueMailbox, graphMessageId, ct);
        string? htmlHash = await TryStoreHtmlAsync(msg.BodyHtml, ct);

        // Resolve requester contact (auto-create if missing).
        var contact = await _contacts.EnsureByEmailAsync(msg.From.Address, msg.From.Name, ct);

        // Create ticket if no thread matched.
        Guid ticketId;
        if (existingTicketId is null)
        {
            var defaults = await ResolveDefaultsAsync(ct);
            if (defaults is null)
            {
                return new MailIngestResult(MailIngestOutcome.SkippedNoDefaults, null, null, null,
                    "No default status/priority configured; cannot create a ticket from mail.");
            }

            // Run the decision tree — thread-reply is already handled by the
            // existingTicketId branch below, so here we only see the "new
            // ticket" paths (primary / single secondary / ambiguous / none).
            var resolution = await _contacts.ResolveCompanyForNewTicketAsync(contact.Id, ct);

            var newTicket = new NewTicket(
                Subject: string.IsNullOrWhiteSpace(msg.Subject) ? "(no subject)" : msg.Subject,
                BodyText: string.Empty,
                BodyHtml: null,
                RequesterContactId: contact.Id,
                QueueId: queueId,
                StatusId: defaults.Value.StatusId,
                PriorityId: defaults.Value.PriorityId,
                CategoryId: null,
                AssigneeUserId: null,
                Source: "Mail",
                CompanyId: resolution.CompanyId,
                AwaitingCompanyAssignment: resolution.Awaiting,
                CompanyResolvedVia: resolution.ResolvedVia);
            var ticket = await _tickets.CreateAsync(newTicket, ct);
            ticketId = ticket.Id;
            await _sla.OnTicketCreatedAsync(ticketId, ct);
        }
        else
        {
            // Thread-reply: the existing ticket keeps its own company_id /
            // resolved_via / awaiting state. We don't re-run the decision tree
            // on replies — a mid-thread "move" would silently reassign tickets
            // and lose audit integrity. Manual reassignment stays the only way
            // to change company post-creation.
            ticketId = existingTicketId.Value;
        }

        // Insert mail_messages + recipients + attachment rows + ingest jobs (one tx).
        var allRecipients = BuildRecipients(msg);
        var newAttachments = msg.Attachments
            .Select(a => new NewMailAttachment(
                GraphAttachmentId: a.Id,
                Mailbox: queueMailbox,
                GraphMessageId: msg.Id,
                FileName: a.Name,
                MimeType: a.ContentType,
                Size: a.Size,
                IsInline: a.IsInline,
                ContentId: a.ContentId))
            .ToList();

        var inlineCount = newAttachments.Count(a => a.IsInline);
        var nonInlineCount = newAttachments.Count - inlineCount;
        _logger.LogInformation(
            "[MailIngest] mailbox={Mailbox} graphMessageId={GraphMessageId} attachments={Total} (inline={Inline}, file={File}) htmlBlob={HtmlPresent}",
            queueMailbox, graphMessageId, newAttachments.Count, inlineCount, nonInlineCount,
            string.IsNullOrWhiteSpace(htmlHash) ? "none" : htmlHash[..Math.Min(8, htmlHash.Length)]);

        var mailId = await _mail.InsertAsync(
            new NewMailMessage(
                MessageId: string.IsNullOrWhiteSpace(msg.InternetMessageId)
                    ? $"generated:{Guid.NewGuid():N}@servicedesk.local"
                    : msg.InternetMessageId,
                InReplyTo: msg.InReplyTo,
                References: msg.References,
                Subject: msg.Subject,
                FromAddress: msg.From.Address,
                FromName: msg.From.Name,
                MailboxAddress: queueMailbox,
                ReceivedUtc: msg.ReceivedUtc.UtcDateTime,
                RawEmlBlobHash: rawHash,
                BodyHtmlBlobHash: htmlHash,
                BodyText: bodyText,
                GraphMessageId: msg.Id),
            allRecipients, newAttachments, ct);

        // Append MailReceived event with metadata snippet.
        var metadata = JsonSerializer.Serialize(new
        {
            from = msg.From.Address,
            fromName = msg.From.Name,
            subject = msg.Subject,
            mail_message_id = mailId,
            internet_message_id = msg.InternetMessageId,
        });
        var evt = await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
            EventType: TicketEventType.MailReceived.ToString(),
            BodyText: snippet,
            BodyHtml: msg.BodyHtml,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: contact.Id,
            MetadataJson: metadata), ct);

        if (evt is null)
        {
            // Ticket disappeared mid-ingest — rare race. Leave mail_messages row
            // unattached so an admin can investigate.
            _logger.LogWarning("Mail {MailId} ingested but ticket {TicketId} not found for event append.",
                mailId, ticketId);
            return new MailIngestResult(
                existingTicketId is null ? MailIngestOutcome.Created : MailIngestOutcome.Appended,
                mailId, ticketId, null, null);
        }

        await _mail.AttachToTicketAsync(mailId, ticketId, evt.Id, ct);

        // Inbound mail is a customer touch — it neither counts as first response
        // nor as a pause-exit, but may affect deadlines if any recalc logic lands
        // later. Calling the engine keeps state fresh without special-casing.
        await _sla.OnTicketEventAsync(ticketId, evt.EventType, ct);

        return new MailIngestResult(
            existingTicketId is null ? MailIngestOutcome.Created : MailIngestOutcome.Appended,
            mailId, ticketId, evt.Id, null);
    }

    private async Task<Guid?> ResolveExistingTicketAsync(GraphFullMessage msg, string token, CancellationToken ct)
    {
        // 1. Plus-address in any recipient.
        var plusRegex = new Regex(
            $@"\+{Regex.Escape(token)}-(\d+)@",
            RegexOptions.IgnoreCase);
        foreach (var r in Enumerable.Concat(msg.To, msg.Cc).Concat(msg.Bcc))
        {
            var m = plusRegex.Match(r.Address);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var number))
            {
                var id = await LookupTicketByNumberAsync(number, ct);
                if (id is not null) return id;
            }
        }

        // 2. In-Reply-To / References matching stored Message-IDs.
        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(msg.InReplyTo)) refs.Add(msg.InReplyTo.Trim().Trim('<', '>'));
        if (!string.IsNullOrWhiteSpace(msg.References))
        {
            foreach (var part in msg.References.Split(new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                refs.Add(part.Trim().Trim('<', '>'));
            }
        }
        // Also include un-trimmed variants (Message-IDs in the DB may or may
        // not carry angle brackets — we store what Graph gave us).
        var all = refs.Concat(refs.Select(r => $"<{r}>")).Distinct().ToList();
        if (all.Count > 0)
        {
            var t = await _mail.FindTicketIdByReferencesAsync(all, ct);
            if (t is not null) return t;
        }

        // 3. Subject token [TCK-1234].
        var subjectRegex = new Regex($@"\[{Regex.Escape(token)}-(\d+)\]", RegexOptions.IgnoreCase);
        var sm = subjectRegex.Match(msg.Subject ?? string.Empty);
        if (sm.Success && long.TryParse(sm.Groups[1].Value, out var subjNumber))
        {
            return await LookupTicketByNumberAsync(subjNumber, ct);
        }

        return null;
    }

    private Task<Guid?> LookupTicketByNumberAsync(long number, CancellationToken ct)
        => _tickets is ITicketNumberLookup l
            ? l.GetIdByNumberAsync(number, ct)
            : Task.FromResult<Guid?>(null);

    private async Task<(Guid StatusId, Guid PriorityId)?> ResolveDefaultsAsync(CancellationToken ct)
    {
        var statuses = await _taxonomy.ListStatusesAsync(ct);
        var priorities = await _taxonomy.ListPrioritiesAsync(ct);
        var status = statuses.FirstOrDefault(s => s.IsDefault && s.IsActive)
                     ?? statuses.FirstOrDefault(s => s.IsActive);
        var priority = priorities.FirstOrDefault(p => p.IsDefault && p.IsActive)
                       ?? priorities.FirstOrDefault(p => p.IsActive);
        if (status is null || priority is null) return null;
        return (status.Id, priority.Id);
    }

    private async Task<string?> TryStoreRawAsync(string mailbox, string graphMessageId, CancellationToken ct)
    {
        try
        {
            await using var raw = await _graph.FetchRawMessageAsync(mailbox, graphMessageId, ct);
            var result = await _blobs.WriteAsync(raw, ct);
            return result.ContentHash;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist raw .eml blob for message {GraphMessageId}", graphMessageId);
            return null;
        }
    }

    private async Task<string?> TryStoreHtmlAsync(string? html, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(html)) return null;
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
            var result = await _blobs.WriteAsync(stream, ct);
            return result.ContentHash;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist body_html blob");
            return null;
        }
    }

    private static IReadOnlyList<NewMailRecipient> BuildRecipients(GraphFullMessage msg)
    {
        var list = new List<NewMailRecipient>(msg.To.Count + msg.Cc.Count + msg.Bcc.Count);
        foreach (var r in msg.To) list.Add(new NewMailRecipient("to", r.Address, r.Name));
        foreach (var r in msg.Cc) list.Add(new NewMailRecipient("cc", r.Address, r.Name));
        foreach (var r in msg.Bcc) list.Add(new NewMailRecipient("bcc", r.Address, r.Name));
        return list;
    }

    private static string ExtractBodyText(GraphFullMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.BodyText)) return msg.BodyText.Trim();
        if (string.IsNullOrEmpty(msg.BodyHtml)) return string.Empty;
        var stripped = TagTrim.Replace(msg.BodyHtml, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return WhitespaceTrim.Replace(decoded, " ").Trim();
    }

    private static string Snippet(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }
}

/// Optional extension point: ticket number → id lookup. Implemented by the
/// production repository; stubs in tests can leave it unimplemented.
public interface ITicketNumberLookup
{
    Task<Guid?> GetIdByNumberAsync(long number, CancellationToken ct);
}
