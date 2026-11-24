using System.Net;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Notifications;

/// What a notification mail says. Every text is plain — the mailer encodes.
/// <paramref name="LinkPath"/> is the in-app path (e.g. <c>/tickets/{id}#event-1</c>)
/// that the button opens, prefixed with <c>App.PublicBaseUrl</c>.
public sealed record NotificationMailContent(
    Guid QueueId,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    string SubjectPrefix,
    string Eyebrow,
    string ActorEmail,
    string ActionText,
    string Preview,
    string LinkPath);

/// Which per-user profile switch governs a notification mail.
/// <paramref name="DefaultOn"/> = mail unless the user stored "false";
/// otherwise only users who stored "true" get it.
public sealed record NotificationMailPreference(string Key, bool DefaultOn);

public interface INotificationMailer
{
    /// One mail per notification row (bell entry), honouring the app-wide
    /// switch, the queue mailbox and each recipient's profile preference.
    /// Every row is stamped (sent time or reason). Best effort: never throws.
    Task SendForRowsAsync(IReadOnlyList<UserNotificationRow> rows, NotificationMailContent content,
        NotificationMailPreference preference, CancellationToken ct);

    /// Plain addresses without a bell row (tagging-only mailboxes). Best effort.
    Task SendToAddressesAsync(IReadOnlyList<string> addresses, NotificationMailContent content, CancellationToken ct);
}

/// v0.1.54 — the one place that turns a notification into a mail: the
/// shared kill-switch <c>Notifications.MentionEmailEnabled</c>, the queue's
/// mailbox as sender, Graph direct send (no MailSent timeline event, no
/// mail_messages row — notifications are not ticket mail), no Reply-To
/// (a reply must not land back as ticket mail) and a plain-text excerpt
/// (inline images need the agent's session). Used by @@-tags and by
/// replies / likes on internal notes.
public sealed class NotificationMailer : INotificationMailer
{
    private const int ExcerptChars = 400;

    private readonly INotificationRepository _repo;
    private readonly IUserService _users;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IGraphMailClient _graph;
    private readonly ISettingsService _settings;
    private readonly ILogger<NotificationMailer> _logger;

    public NotificationMailer(
        INotificationRepository repo, IUserService users, ITaxonomyRepository taxonomy,
        IGraphMailClient graph, ISettingsService settings, ILogger<NotificationMailer> logger)
    {
        _repo = repo;
        _users = users;
        _taxonomy = taxonomy;
        _graph = graph;
        _settings = settings;
        _logger = logger;
    }

    private sealed record Channel(string FromMailbox, string PublicBaseUrl);

    /// Null = mail is off app-wide or the queue has no mailbox (logged).
    private async Task<(Channel? Channel, string? Reason)> ResolveChannelAsync(Guid queueId, Guid ticketId, CancellationToken ct)
    {
        if (!await _settings.GetAsync<bool>(SettingKeys.Notifications.MentionEmailEnabled, ct))
            return (null, null);
        var queue = await _taxonomy.GetQueueAsync(queueId, ct);
        var from = FirstNonEmpty(queue?.OutboundMailboxAddress, queue?.InboundMailboxAddress);
        if (string.IsNullOrWhiteSpace(from))
        {
            _logger.LogWarning(
                "Queue {QueueId} has no outbound/inbound mailbox; skipping notification emails for ticket {TicketId}.",
                queueId, ticketId);
            return (null, "no mailbox configured on queue");
        }
        var publicBaseUrl = await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            _logger.LogWarning(
                "App.PublicBaseUrl is empty; notification-mail CTA will be a relative path. Set this setting so the link survives outside the browser session.");
        }
        return (new Channel(from, publicBaseUrl), null);
    }

    public async Task SendForRowsAsync(IReadOnlyList<UserNotificationRow> rows, NotificationMailContent content,
        NotificationMailPreference preference, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var (channel, reason) = await ResolveChannelAsync(content.QueueId, content.TicketId, ct);
        if (channel is null)
        {
            if (reason is null)
            {
                _logger.LogInformation(
                    "Notification email is globally disabled (Notifications.MentionEmailEnabled=false); skipped {Count} sends.",
                    rows.Count);
                return;
            }
            foreach (var row in rows) await _repo.MarkEmailSentAsync(row.Id, null, reason, ct);
            return;
        }

        // A failed preference lookup falls back to the switch's default
        // rather than silently dropping (or forcing) every mail.
        IReadOnlySet<Guid> noMail;
        try
        {
            noMail = await _repo.ListMailOptOutsAsync(preference.Key, preference.DefaultOn, rows.Select(r => r.UserId).ToList(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read mail preference {Key}; using its default.", preference.Key);
            noMail = preference.DefaultOn ? new HashSet<Guid>() : rows.Select(r => r.UserId).ToHashSet();
        }

        await Parallel.ForEachAsync(rows, SendOptions(ct), async (row, token) =>
        {
            if (noMail.Contains(row.UserId))
            {
                await _repo.MarkEmailSentAsync(row.Id, null, "mail turned off in profile", token);
                return;
            }
            var recipient = await _users.FindByIdAsync(row.UserId, token);
            if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email))
            {
                await _repo.MarkEmailSentAsync(row.Id, null, "recipient user not found", token);
                return;
            }
            try
            {
                var result = await _graph.SendMailAsync(BuildMessage(channel, content, recipient.Email), token);
                await _repo.MarkEmailSentAsync(row.Id, result.SentUtc.UtcDateTime, null, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Failed to send notification mail for notification {NotificationId} (user {UserId}, ticket {TicketId}).",
                    row.Id, row.UserId, content.TicketId);
                await _repo.MarkEmailSentAsync(row.Id, null, Truncate(ex.Message, 400), token);
            }
        });
    }

    public async Task SendToAddressesAsync(IReadOnlyList<string> addresses, NotificationMailContent content, CancellationToken ct)
    {
        if (addresses.Count == 0) return;
        var (channel, _) = await ResolveChannelAsync(content.QueueId, content.TicketId, ct);
        if (channel is null) return;
        await Parallel.ForEachAsync(addresses, SendOptions(ct), async (address, token) =>
        {
            try
            {
                await _graph.SendMailAsync(BuildMessage(channel, content, address), token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to send notification mail to {Email} for ticket {TicketId}.",
                    address, content.TicketId);
            }
        });
    }

    /// One mail per recipient, sent side by side: a tag to five people costs
    /// about one Graph round trip instead of five, while the agent's request
    /// waits (Logs10, 2026-11-24: ~115 ms per send). Four at a time is
    /// Exchange Online's concurrent-request limit per mailbox — every send
    /// here goes out of the same queue mailbox, so going higher only buys
    /// throttled (429) requests.
    private static ParallelOptions SendOptions(CancellationToken ct) =>
        new() { MaxDegreeOfParallelism = 4, CancellationToken = ct };

    private static GraphOutboundMessage BuildMessage(Channel channel, NotificationMailContent c, string recipientEmail)
        => new(
            FromMailbox: channel.FromMailbox,
            Subject: $"{c.SubjectPrefix}: {c.TicketSubject} [#{c.TicketNumber}]",
            BodyHtml: BuildBody(c, recipientEmail, channel.PublicBaseUrl),
            To: new[] { new GraphRecipient(recipientEmail, recipientEmail) },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            ReplyTo: Array.Empty<GraphRecipient>(),
            Attachments: null);

    private static string BuildBody(NotificationMailContent c, string recipientEmail, string publicBaseUrl)
    {
        var subject = WebUtility.HtmlEncode(c.TicketSubject ?? string.Empty);
        var actor = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(c.ActorEmail) ? "unknown" : c.ActorEmail);
        var eyebrow = WebUtility.HtmlEncode(c.Eyebrow);
        var action = WebUtility.HtmlEncode(c.ActionText);
        var recipient = WebUtility.HtmlEncode(recipientEmail ?? string.Empty);
        var preview = WebUtility.HtmlEncode(Truncate(c.Preview ?? string.Empty, ExcerptChars));
        var href = string.IsNullOrWhiteSpace(publicBaseUrl) ? c.LinkPath : $"{publicBaseUrl.TrimEnd('/')}{c.LinkPath}";
        var previewBlock = preview.Length == 0 ? string.Empty
            : $"""<div style="padding:12px 14px;background:#f6f5fc;border-left:3px solid rgba(139,92,246,0.55);border-radius:6px;font-size:14px;color:#41444e;white-space:pre-wrap;line-height:1.5;">{preview}</div>""";

        // Light, email-client-safe palette with inline styles only (Outlook
        // and Gmail strip <style> blocks and CSS variables).
        return $$"""
            <!DOCTYPE html>
            <html><body style="margin:0;padding:24px;background:#f4f5f9;font-family:Inter,-apple-system,Segoe UI,Roboto,sans-serif;color:#2a2d35;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e6e7ee;border-radius:14px;overflow:hidden;">
                <tr><td style="padding:20px 24px;background:linear-gradient(135deg,rgba(139,92,246,0.12),rgba(59,130,246,0.08));border-bottom:1px solid #ececf3;">
                  <div style="font-size:12px;color:#6b6f7a;letter-spacing:0.04em;text-transform:uppercase;margin-bottom:6px;">{{eyebrow}}</div>
                  <div style="font-size:18px;font-weight:600;color:#1b1e27;">Ticket #{{c.TicketNumber}} — {{subject}}</div>
                </td></tr>
                <tr><td style="padding:20px 24px;">
                  <div style="font-size:14px;color:#3a3d47;margin-bottom:14px;">
                    <strong style="color:#6d4ad1;">{{actor}}</strong> {{action}}
                  </div>
                  {{previewBlock}}
                  <div style="margin-top:22px;text-align:center;">
                    <a href="{{href}}" style="display:inline-block;padding:11px 22px;background:#8b5cf6;color:#fff;font-weight:600;text-decoration:none;border-radius:8px;font-size:14px;">Open ticket</a>
                  </div>
                </td></tr>
                <tr><td style="padding:14px 24px;border-top:1px solid #ececf3;font-size:12px;color:#8b8e99;">
                  Sent to {{recipient}} · Servicedesk
                </td></tr>
              </table>
            </body></html>
            """;
    }

    private static string? FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

    internal static string Truncate(string s, int maxLength)
        => string.IsNullOrEmpty(s) || s.Length <= maxLength ? s : s.Substring(0, maxLength - 1) + "…";
}
