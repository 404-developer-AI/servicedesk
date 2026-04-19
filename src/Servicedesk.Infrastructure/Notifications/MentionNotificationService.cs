using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Notifications;

public sealed class MentionNotificationService : IMentionNotificationService
{
    private const int PreviewCharLimit = 200;
    private const int MailBodyExcerptChars = 400;

    // Same two-step HTML→text stripper used by OutboundMailService: strip
    // tags, decode entities, collapse whitespace. Handles the common case
    // where the events-endpoint only sends bodyHtml (no plain-text twin).
    private static readonly Regex TagStrip = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceCollapse = new(@"\s+", RegexOptions.Compiled);

    private readonly INotificationRepository _repo;
    private readonly IUserNotifier _notifier;
    private readonly IUserService _users;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IGraphMailClient _graph;
    private readonly ISettingsService _settings;
    private readonly ILogger<MentionNotificationService> _logger;

    public MentionNotificationService(
        INotificationRepository repo,
        IUserNotifier notifier,
        IUserService users,
        ITaxonomyRepository taxonomy,
        IGraphMailClient graph,
        ISettingsService settings,
        ILogger<MentionNotificationService> logger)
    {
        _repo = repo;
        _notifier = notifier;
        _users = users;
        _taxonomy = taxonomy;
        _graph = graph;
        _settings = settings;
        _logger = logger;
    }

    public async Task PublishAsync(MentionNotificationSource source, CancellationToken ct)
    {
        if (source.MentionedUserIds.Count == 0) return;

        // Fall back to a stripped HTML body when BodyText is empty. The
        // events-endpoint (Note / Comment) sends only BodyHtml; OutboundMail
        // already strips its own HTML before calling us, so the fallback is
        // a no-op there. Keeping it here means the mail preview + navbar
        // snippet always carry something, regardless of caller.
        var effectiveBodyText = !string.IsNullOrWhiteSpace(source.BodyText)
            ? source.BodyText
            : HtmlToText(source.BodyHtml);

        var distinctIds = source.MentionedUserIds.Distinct().ToList();
        var previewText = BuildPreview(effectiveBodyText);
        var rows = distinctIds
            .Select(uid => new NewUserNotification(
                UserId: uid,
                SourceUserId: source.SourceUserId,
                NotificationType: "mention",
                TicketId: source.TicketId,
                TicketNumber: source.TicketNumber,
                TicketSubject: source.TicketSubject,
                EventId: source.EventId,
                EventType: source.EventType,
                PreviewText: previewText))
            .ToList();

        IReadOnlyList<UserNotificationRow> inserted;
        try
        {
            inserted = await _repo.CreateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            // Hard fail at the DB level — we log and return without throwing
            // so the originating ticket-event / outbound-mail is not rolled
            // back. Losing a notification row is strictly better than losing
            // the post itself.
            _logger.LogError(ex,
                "Failed to persist mention-notifications for ticket {TicketId} event {EventId}.",
                source.TicketId, source.EventId);
            return;
        }

        // Real-time push — one fan-out per inserted row. Failures are per-row
        // and swallowed; a disconnected agent simply doesn't get the live
        // toast but will see the navbar entry on their next page load.
        foreach (var row in inserted)
        {
            var payload = new UserNotificationPush(
                Id: row.Id,
                TicketId: row.TicketId,
                TicketNumber: row.TicketNumber,
                TicketSubject: row.TicketSubject,
                SourceUserEmail: row.SourceUserEmail,
                EventId: row.EventId,
                EventType: row.EventType,
                PreviewText: row.PreviewText,
                CreatedUtc: row.CreatedUtc);
            try
            {
                await _notifier.NotifyMentionAsync(row.UserId, payload, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SignalR push failed for notification {NotificationId} (user {UserId}).",
                    row.Id, row.UserId);
            }
        }

        // Mail — kill-switch + per-row best-effort. One mail per recipient.
        var mailEnabled = await _settings.GetAsync<bool>(SettingKeys.Notifications.MentionEmailEnabled, ct);
        if (!mailEnabled)
        {
            _logger.LogInformation(
                "Mention-notification email is globally disabled (Notifications.MentionEmailEnabled=false); skipped {Count} sends.",
                inserted.Count);
            return;
        }

        var queue = await _taxonomy.GetQueueAsync(source.QueueId, ct);
        var fromMailbox = FirstNonEmpty(queue?.OutboundMailboxAddress, queue?.InboundMailboxAddress);
        if (string.IsNullOrWhiteSpace(fromMailbox))
        {
            // No mailbox configured on the queue — stamp each row so the
            // history page can show a "no mailbox" badge, and return.
            _logger.LogWarning(
                "Queue {QueueId} has no outbound/inbound mailbox; skipping notification emails for ticket {TicketId}.",
                source.QueueId, source.TicketId);
            foreach (var row in inserted)
            {
                await _repo.MarkEmailSentAsync(row.Id, null, "no mailbox configured on queue", ct);
            }
            return;
        }

        var fromName = !string.IsNullOrWhiteSpace(queue?.Name) ? queue!.Name : fromMailbox;
        var publicBaseUrl = await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            _logger.LogWarning(
                "App.PublicBaseUrl is empty; notification-mail CTA will be a relative path. Set this setting so the link survives outside the browser session.");
        }

        foreach (var row in inserted)
        {
            // Resolve the recipient email via the shared user-service. If the
            // user can't be resolved (race with delete) we mark the row with
            // an explicit error — the in-app row still exists so the
            // recipient sees it if/when they reappear.
            var recipient = await _users.FindByIdAsync(row.UserId, ct);
            if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email))
            {
                await _repo.MarkEmailSentAsync(row.Id, null, "recipient user not found", ct);
                continue;
            }

            var subject = $"Tagged: {source.TicketSubject} [#{source.TicketNumber}]";
            var bodyHtml = BuildMailBodyHtml(source, effectiveBodyText, row, recipient.Email, publicBaseUrl);

            var message = new GraphOutboundMessage(
                FromMailbox: fromMailbox,
                Subject: subject,
                BodyHtml: bodyHtml,
                To: new[] { new GraphRecipient(recipient.Email, recipient.Email) },
                Cc: Array.Empty<GraphRecipient>(),
                Bcc: Array.Empty<GraphRecipient>(),
                // Deliberately no Reply-To plus-address: replies to a
                // notification mail should not land as ticket-mail.
                ReplyTo: Array.Empty<GraphRecipient>(),
                Attachments: null);

            try
            {
                var result = await _graph.SendMailAsync(message, ct);
                await _repo.MarkEmailSentAsync(row.Id, result.SentUtc.UtcDateTime, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to send mention-notification mail for notification {NotificationId} (user {UserId}, ticket {TicketId}).",
                    row.Id, row.UserId, source.TicketId);
                await _repo.MarkEmailSentAsync(row.Id, null, Truncate(ex.Message, 400), ct);
            }
        }
    }

    private static string BuildPreview(string bodyText)
    {
        var t = (bodyText ?? string.Empty).Trim();
        return t.Length <= PreviewCharLimit ? t : t.Substring(0, PreviewCharLimit - 1) + "…";
    }

    private static string BuildMailBodyHtml(
        MentionNotificationSource source,
        string bodyText,
        UserNotificationRow row,
        string recipientEmail,
        string publicBaseUrl)
    {
        // Premium look: centered glass-card feel, purple accent matching the
        // in-app mention chip. Plain-text excerpt rather than raw body-HTML
        // because inline images in the source body reference authenticated
        // `/api/tickets/.../attachments/{id}` URLs that won't resolve outside
        // the agent's browser session — embedding them would show broken
        // images. The CTA button takes the agent to the live ticket.
        var encodedSubject = WebUtility.HtmlEncode(source.TicketSubject ?? "");
        var encodedSource = WebUtility.HtmlEncode(source.SourceUserEmail ?? "unknown");
        var encodedRecipient = WebUtility.HtmlEncode(recipientEmail ?? "");
        var encodedPreview = WebUtility.HtmlEncode(Truncate(bodyText ?? string.Empty, MailBodyExcerptChars));
        var eventLabel = source.EventType switch
        {
            "Note" => "internal note",
            "Comment" => "reply",
            "MailSent" => "outbound mail",
            _ => source.EventType.ToLowerInvariant(),
        };

        var relativePath = $"/tickets/{source.TicketId}#event-{source.EventId}";
        var ctaHref = string.IsNullOrWhiteSpace(publicBaseUrl)
            ? relativePath
            : $"{publicBaseUrl.TrimEnd('/')}{relativePath}";

        return $$"""
            <!DOCTYPE html>
            <html><body style="margin:0;padding:24px;background:#0f0f14;font-family:Inter,-apple-system,Segoe UI,Roboto,sans-serif;color:#e9e9ec;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;margin:0 auto;background:#16161c;border:1px solid rgba(255,255,255,0.08);border-radius:14px;overflow:hidden;">
                <tr><td style="padding:20px 24px;background:linear-gradient(135deg,rgba(139,92,246,0.18),rgba(59,130,246,0.12));border-bottom:1px solid rgba(255,255,255,0.06);">
                  <div style="font-size:12px;color:#a3a3ad;letter-spacing:0.04em;text-transform:uppercase;margin-bottom:6px;">You were tagged</div>
                  <div style="font-size:18px;font-weight:600;color:#fafaff;">Ticket #{{source.TicketNumber}} — {{encodedSubject}}</div>
                </td></tr>
                <tr><td style="padding:20px 24px;">
                  <div style="font-size:14px;color:#cfcfd5;margin-bottom:14px;">
                    <strong style="color:#d7c5ff;">{{encodedSource}}</strong> tagged you in {{eventLabel}} on this ticket.
                  </div>
                  <div style="padding:12px 14px;background:rgba(255,255,255,0.03);border-left:3px solid rgba(139,92,246,0.55);border-radius:6px;font-size:14px;color:#d5d5db;white-space:pre-wrap;line-height:1.5;">{{encodedPreview}}</div>
                  <div style="margin-top:22px;text-align:center;">
                    <a href="{{ctaHref}}" style="display:inline-block;padding:11px 22px;background:#8b5cf6;color:#fff;font-weight:600;text-decoration:none;border-radius:8px;font-size:14px;">Open ticket</a>
                  </div>
                </td></tr>
                <tr><td style="padding:14px 24px;border-top:1px solid rgba(255,255,255,0.06);font-size:12px;color:#8b8b92;">
                  Sent to {{encodedRecipient}} · Servicedesk
                </td></tr>
              </table>
            </body></html>
            """;
    }

    private static string? FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

    private static string Truncate(string s, int maxLength)
        => string.IsNullOrEmpty(s) || s.Length <= maxLength
            ? s
            : s.Substring(0, maxLength - 1) + "…";

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var stripped = TagStrip.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return WhitespaceCollapse.Replace(decoded, " ").Trim();
    }
}
