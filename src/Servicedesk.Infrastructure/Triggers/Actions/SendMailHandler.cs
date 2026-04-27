using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Trigger-fired outbound mail. Bypasses <see cref="Mail.Outbound.OutboundMailService"/>
/// because that service is built around a mandatory agent author (mention
/// notifications, intake-form finalisation, attachment-staging) that does
/// not apply to system-actor triggers. This handler shares only
/// <see cref="IGraphMailClient"/> for the Graph send + the same
/// <c>mail_messages</c> insert pattern so inbound replies still thread
/// back through <see cref="MailMessageRepository"/> the usual way.
internal sealed class SendMailHandler : ITriggerActionHandler
{
    private static readonly Regex AnyTicketTag = new(@"\[#\d+\]", RegexOptions.Compiled);
    private static readonly Regex TagTrim = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceTrim = new(@"\s+", RegexOptions.Compiled);

    private readonly IGraphMailClient _graph;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ITicketRepository _tickets;
    private readonly IMailMessageRepository _mail;
    private readonly ICompanyRepository _companies;
    private readonly IUserService _users;
    private readonly IQueueAccessService _queueAccess;
    private readonly ISettingsService _settings;
    private readonly ISlaEngine _sla;
    private readonly TriggerMailDedupTracker _dedup;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly ILogger<SendMailHandler> _logger;

    public SendMailHandler(
        IGraphMailClient graph,
        ITaxonomyRepository taxonomy,
        ITicketRepository tickets,
        IMailMessageRepository mail,
        ICompanyRepository companies,
        IUserService users,
        IQueueAccessService queueAccess,
        ISettingsService settings,
        ISlaEngine sla,
        TriggerMailDedupTracker dedup,
        ITriggerTemplateRenderer renderer,
        ILogger<SendMailHandler> logger)
    {
        _graph = graph;
        _taxonomy = taxonomy;
        _tickets = tickets;
        _mail = mail;
        _companies = companies;
        _users = users;
        _queueAccess = queueAccess;
        _settings = settings;
        _sla = sla;
        _dedup = dedup;
        _renderer = renderer;
        _logger = logger;
    }

    public string Kind => "send_mail";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadString(actionJson, "to", out var toSpec))
            return TriggerActionResult.Failed(Kind, "Action requires 'to' (one of: customer, owner-agent, queue-agents, address:foo@bar.com).");
        if (!ActionJson.TryReadString(actionJson, "subject", out var rawSubject))
            return TriggerActionResult.Failed(Kind, "Action requires 'subject'.");
        if (!ActionJson.TryReadString(actionJson, "body_html", out var bodyHtml))
            return TriggerActionResult.Failed(Kind, "Action requires 'body_html'.");

        if (ctx.RenderContext is { } rc)
        {
            // Subject + headers go through PlainText so a CR/LF in a
            // substituted value can never close the subject line and
            // smuggle a Bcc:; body_html runs through Html so a customer
            // name containing markup lands as inert text, not live HTML.
            rawSubject = _renderer.Render(rawSubject, TemplateEscapeMode.PlainText, rc);
            bodyHtml = _renderer.Render(bodyHtml, TemplateEscapeMode.Html, rc);
        }

        // Recipient resolution. An empty resolved list is a NoOp, not a
        // failure — e.g. owner-agent on a ticket with no assignee.
        var recipients = await ResolveRecipientsAsync(toSpec, ctx.Ticket, ct);
        if (recipients is null)
            return TriggerActionResult.Failed(Kind, $"Unknown recipient spec '{toSpec}'.");
        if (recipients.Count == 0)
            return TriggerActionResult.NoOp(Kind, new { reason = $"No recipients resolved for '{toSpec}'." });

        var fingerprint = BuildFingerprint(recipients, rawSubject, bodyHtml);
        var allowed = await _dedup.ShouldSendAsync(ctx.TriggerId, ctx.TicketId, fingerprint, ct);
        if (!allowed)
            return TriggerActionResult.NoOp(Kind, new { reason = "Suppressed by mail-dedup window." });

        var queue = await _taxonomy.GetQueueAsync(ctx.Ticket.QueueId, ct);
        var fromMailbox = FirstNonEmpty(queue?.OutboundMailboxAddress, queue?.InboundMailboxAddress);
        if (string.IsNullOrWhiteSpace(fromMailbox))
            return TriggerActionResult.Failed(Kind, "Queue has no inbound or outbound mailbox configured.");

        var plusToken = await _settings.GetAsync<string>(SettingKeys.Mail.PlusAddressToken, ct);
        if (string.IsNullOrWhiteSpace(plusToken)) plusToken = "TCK";
        var replyToAddress = BuildPlusAddress(fromMailbox!, plusToken!, ctx.Ticket.Number);
        var fromName = !string.IsNullOrWhiteSpace(queue?.Name) ? queue!.Name : fromMailbox!;
        var subject = NormalizeSubject(rawSubject, ctx.Ticket.Number);
        var anchor = await _mail.GetLatestThreadAnchorAsync(ctx.TicketId, ct);

        var graphMsg = new GraphOutboundMessage(
            FromMailbox: fromMailbox!,
            Subject: subject,
            BodyHtml: bodyHtml,
            To: recipients,
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            ReplyTo: new[] { new GraphRecipient(replyToAddress, fromName) },
            Attachments: null,
            // Microsoft Graph only persists custom headers prefixed with
            // x-, so we use X-Auto-Submitted; the GraphMailClient inbound
            // path treats X-Auto-Submitted and Auto-Submitted as
            // equivalent so a reply chain that echoes either header back
            // is skipped on the next ingest cycle.
            InternetMessageHeaders: new[]
            {
                new GraphOutboundHeader("X-Auto-Submitted", "auto-generated"),
                new GraphOutboundHeader("X-Servicedesk-Triggered-By", ctx.TriggerId.ToString()),
            });

        GraphSentMailResult sendResult;
        try
        {
            sendResult = await _graph.SendMailAsync(graphMsg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trigger {TriggerId} send_mail failed for ticket {TicketId}.", ctx.TriggerId, ctx.TicketId);
            return TriggerActionResult.Failed(Kind, $"Graph send failed: {ex.GetType().Name}: {ex.Message}");
        }

        var bodyText = HtmlToText(bodyHtml);
        var metadata = JsonSerializer.Serialize(new
        {
            triggered_by = ctx.TriggerId,
            kind = "trigger",
            from = fromMailbox,
            fromName,
            replyTo = replyToAddress,
            subject,
            to = recipients.Select(r => new { address = r.Address, name = r.Name }),
            internet_message_id = sendResult.InternetMessageId,
            in_reply_to = anchor?.MessageId,
        });

        var evt = await _tickets.AddEventAsync(ctx.TicketId, new NewTicketEvent(
            EventType: TicketEventType.MailSent.ToString(),
            BodyText: bodyText,
            BodyHtml: bodyHtml,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: null,
            MetadataJson: metadata), ct);

        if (evt is null)
        {
            _logger.LogWarning(
                "Trigger {TriggerId}: outbound mail sent (internet-message-id {MsgId}) but ticket {TicketId} disappeared before MailSent event could be persisted.",
                ctx.TriggerId, sendResult.InternetMessageId, ctx.TicketId);
            return TriggerActionResult.Failed(Kind, "Mail sent but ticket vanished; no event persisted.");
        }

        var mailRecipients = recipients.Select(r => new NewMailRecipient("to", r.Address, r.Name)).ToList();
        await _mail.InsertOutboundAsync(new NewOutboundMailMessage(
            MessageId: sendResult.InternetMessageId,
            InReplyTo: anchor?.MessageId,
            References: ComposeReferences(anchor),
            Subject: subject,
            FromAddress: fromMailbox!,
            FromName: fromName,
            MailboxAddress: fromMailbox!,
            SentUtc: sendResult.SentUtc.UtcDateTime,
            BodyText: bodyText,
            TicketId: ctx.TicketId,
            TicketEventId: evt.Id), mailRecipients, ct);

        await _sla.OnTicketEventAsync(ctx.TicketId, evt.EventType, ct);

        return TriggerActionResult.Applied(Kind, new
        {
            eventId = evt.Id,
            recipients = recipients.Select(r => r.Address),
            internetMessageId = sendResult.InternetMessageId,
        });
    }

    /// Returns null if the spec is unrecognised; an empty list when the
    /// spec is valid but resolves to no addresses (treated as NoOp by the
    /// caller).
    private async Task<IReadOnlyList<GraphRecipient>?> ResolveRecipientsAsync(
        string toSpec, Domain.Tickets.Ticket ticket, CancellationToken ct)
    {
        toSpec = toSpec.Trim();

        if (string.Equals(toSpec, "customer", StringComparison.OrdinalIgnoreCase))
        {
            var contact = await _companies.GetContactAsync(ticket.RequesterContactId, ct);
            if (contact is null || string.IsNullOrWhiteSpace(contact.Email)) return Array.Empty<GraphRecipient>();
            var name = $"{contact.FirstName} {contact.LastName}".Trim();
            return new[] { new GraphRecipient(contact.Email, string.IsNullOrWhiteSpace(name) ? contact.Email : name) };
        }

        if (string.Equals(toSpec, "owner-agent", StringComparison.OrdinalIgnoreCase))
        {
            if (!ticket.AssigneeUserId.HasValue) return Array.Empty<GraphRecipient>();
            var user = await _users.FindByIdAsync(ticket.AssigneeUserId.Value, ct);
            if (user is null || string.IsNullOrWhiteSpace(user.Email)) return Array.Empty<GraphRecipient>();
            return new[] { new GraphRecipient(user.Email, user.Email) };
        }

        if (string.Equals(toSpec, "queue-agents", StringComparison.OrdinalIgnoreCase))
        {
            var userIds = await _queueAccess.GetUsersForQueueAsync(ticket.QueueId, ct);
            if (userIds.Count == 0) return Array.Empty<GraphRecipient>();
            var list = new List<GraphRecipient>(userIds.Count);
            foreach (var uid in userIds)
            {
                var u = await _users.FindByIdAsync(uid, ct);
                if (u is null || string.IsNullOrWhiteSpace(u.Email)) continue;
                list.Add(new GraphRecipient(u.Email, u.Email));
            }
            return list;
        }

        if (toSpec.StartsWith("address:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = toSpec.Substring("address:".Length).Trim();
            if (raw.Length == 0) return Array.Empty<GraphRecipient>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<GraphRecipient>(parts.Length);
            foreach (var p in parts)
            {
                if (!IsValidEmail(p)) continue;
                list.Add(new GraphRecipient(p, p));
            }
            return list;
        }

        return null;
    }

    private static string BuildFingerprint(IReadOnlyList<GraphRecipient> recipients, string subject, string bodyHtml)
    {
        var ordered = recipients
            .Select(r => r.Address.Trim().ToLowerInvariant())
            .OrderBy(a => a, StringComparer.Ordinal);
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        sb.Append(string.Join(",", ordered));
        sb.Append('|');
        sb.Append(subject.Trim());
        sb.Append('|');
        sb.Append(bodyHtml.Length.ToString(CultureInfo.InvariantCulture));
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeSubject(string subject, long ticketNumber)
    {
        var clean = (subject ?? string.Empty).Trim();
        var tag = $"[#{ticketNumber}]";
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

    private static bool IsValidEmail(string s)
    {
        try { var _ = new MailAddress(s); return true; }
        catch { return false; }
    }
}
