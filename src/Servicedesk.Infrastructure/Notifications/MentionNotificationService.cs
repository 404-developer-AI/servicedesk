using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.TagGroups;

namespace Servicedesk.Infrastructure.Notifications;

public sealed class MentionNotificationService : IMentionNotificationService
{
    private const int PreviewCharLimit = 200;

    // Same two-step HTML→text stripper used by OutboundMailService: strip
    // tags, decode entities, collapse whitespace. Handles the common case
    // where the events-endpoint only sends bodyHtml (no plain-text twin).
    private static readonly Regex TagStrip = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceCollapse = new(@"\s+", RegexOptions.Compiled);

    private readonly INotificationRepository _repo;
    private readonly IUserNotifier _notifier;
    private readonly INotificationMailer _mailer;
    private readonly ITaggingMailboxRepository _taggingMailboxes;
    private readonly ITagGroupRepository _tagGroups;
    private readonly IGroupTagRepository _groupTags;
    private readonly IGroupTagNotifier _groupTagNotifier;
    private readonly ILogger<MentionNotificationService> _logger;

    public MentionNotificationService(
        INotificationRepository repo,
        IUserNotifier notifier,
        INotificationMailer mailer,
        ITaggingMailboxRepository taggingMailboxes,
        ITagGroupRepository tagGroups,
        IGroupTagRepository groupTags,
        IGroupTagNotifier groupTagNotifier,
        ILogger<MentionNotificationService> logger)
    {
        _repo = repo;
        _notifier = notifier;
        _mailer = mailer;
        _taggingMailboxes = taggingMailboxes;
        _tagGroups = tagGroups;
        _groupTags = groupTags;
        _groupTagNotifier = groupTagNotifier;
        _logger = logger;
    }

    public async Task PublishAsync(MentionNotificationSource source, CancellationToken ct)
    {
        var groupIds = source.MentionedGroupIds ?? Array.Empty<Guid>();
        if (source.MentionedUserIds.Count == 0 && source.MentionedMailboxIds.Count == 0 && groupIds.Count == 0) return;

        // Fall back to a stripped HTML body when BodyText is empty. The
        // events-endpoint (Note / Comment) sends only BodyHtml; OutboundMail
        // already strips its own HTML before calling us, so the fallback is
        // a no-op there. Keeping it here means the mail preview + navbar
        // snippet always carry something, regardless of caller.
        var effectiveBodyText = !string.IsNullOrWhiteSpace(source.BodyText)
            ? source.BodyText
            : HtmlToText(source.BodyHtml);

        // --- In-app + SignalR for mentioned USERS (mailboxes get neither) ---
        IReadOnlyList<UserNotificationRow> inserted = Array.Empty<UserNotificationRow>();
        if (source.MentionedUserIds.Count > 0)
        {
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

            try
            {
                inserted = await _repo.CreateManyAsync(rows, ct);
            }
            catch (Exception ex)
            {
                // Hard fail at the DB level — log and carry on (mailbox mails
                // below still go out) so the originating ticket-event /
                // outbound-mail is not rolled back. Losing a notification row
                // is strictly better than losing the post itself.
                _logger.LogError(ex,
                    "Failed to persist mention-notifications for ticket {TicketId} event {EventId}.",
                    source.TicketId, source.EventId);
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
        }

        // --- Group tags (v0.1.54): one shared row per group, NEVER a mail ---
        if (groupIds.Count > 0)
            await PublishGroupTagsAsync(source, groupIds, effectiveBodyText, ct);
        if (source.MentionedUserIds.Count == 0 && source.MentionedMailboxIds.Count == 0) return;

        // --- Mail (users + tagging mailboxes) via the shared notification mailer ---
        var mail = new NotificationMailContent(
            QueueId: source.QueueId,
            TicketId: source.TicketId,
            TicketNumber: source.TicketNumber,
            TicketSubject: source.TicketSubject,
            SubjectPrefix: "Tagged",
            Eyebrow: "You were tagged",
            ActorEmail: source.SourceUserEmail,
            ActionText: $"tagged you in {EventLabel(source.EventType)} on this ticket.",
            Preview: effectiveBodyText,
            LinkPath: $"/tickets/{source.TicketId}#event-{source.EventId}");
        await _mailer.SendForRowsAsync(inserted, mail, NotificationPreferenceKeys.MentionEmailPreference, ct);

        // Tagging-only mailboxes: a mail only — no DB row / SignalR, they are
        // not users; the mention is purely "send a copy to this address".
        if (source.MentionedMailboxIds.Count > 0)
        {
            var mailboxes = await _taggingMailboxes.ResolveActiveByIdsAsync(source.MentionedMailboxIds, ct);
            await _mailer.SendToAddressesAsync(mailboxes.Select(m => m.Email).ToList(), mail, ct);
        }
    }

    private static string EventLabel(string eventType) => eventType switch
    {
        "Note" => "internal note",
        "Comment" => "reply",
        "Call" => "phone call",
        "MailSent" => "outbound mail",
        "NoteReply" => "a reply on an internal note",
        _ => eventType.ToLowerInvariant(),
    };

    /// One shared group_tags row per active group, then a block refresh for every
    /// member and a toast for every member but the author. Deliberately no mail path at all. Best effort
    /// like the rest of the fan-out: a failure is logged, never thrown.
    private async Task PublishGroupTagsAsync(
        MentionNotificationSource source, IReadOnlyList<Guid> groupIds, string bodyText, CancellationToken ct)
    {
        try
        {
            var groups = await _tagGroups.ResolveActiveByIdsAsync(groupIds, ct);
            if (groups.Count == 0) return;
            var preview = BuildPreview(bodyText);
            var created = await _groupTags.CreateManyAsync(groups.Select(g => new NewGroupTag(
                g.Id, source.TicketId, source.TicketNumber, source.TicketSubject,
                source.EventId, source.EventType, source.SourceUserId, preview)).ToList(), ct);
            var members = await _tagGroups.ListMemberIdsAsync(groups.Select(g => g.Id).ToList(), ct);
            foreach (var tag in created)
            {
                var all = members.GetValueOrDefault(tag.GroupId) ?? Array.Empty<Guid>();
                var recipients = all.Where(u => u != source.SourceUserId).ToList();
                try
                {
                    // Every member's block refreshes — the author's too when
                    // they belong to the group; only the others get a toast.
                    await _groupTagNotifier.GroupTagsChangedAsync(all, ct);
                    await _groupTagNotifier.GroupTagReceivedAsync(recipients, new GroupTagPush(
                        tag.Id, tag.GroupId, tag.GroupName, tag.TicketId, tag.TicketNumber, tag.TicketSubject,
                        tag.EventId, source.SourceUserEmail, tag.PreviewText), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Group tag push failed for tag {TagId}.", tag.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write group tags for ticket {TicketId} event {EventId}.",
                source.TicketId, source.EventId);
        }
    }

    private static string BuildPreview(string bodyText)
    {
        var t = (bodyText ?? string.Empty).Trim();
        return t.Length <= PreviewCharLimit ? t : t.Substring(0, PreviewCharLimit - 1) + "…";
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var stripped = TagStrip.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return WhitespaceCollapse.Replace(decoded, " ").Trim();
    }
}
