using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Infrastructure.Tickets.NoteThreads;

public static class NoteThreadRejectCode
{
    /// Ticket / note / reply missing, no queue access, or not an internal note — 404.
    public const string NotFound = "not_found";
    public const string Invalid = "invalid";
    public const string Forbidden = "forbidden";
}

public sealed class NoteThreadRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record NoteReplyInput(
    long? ParentReplyId, string? BodyHtml, IReadOnlyList<Guid>? MentionedUserIds, IReadOnlyList<Guid>? MentionedGroupIds);

public interface INoteThreadService
{
    Task<NoteThreadData> GetAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    Task<NoteReply> AddReplyAsync(TicketMutationActor actor, Guid ticketId, long eventId, NoteReplyInput input, CancellationToken ct);
    Task<NoteReply> EditReplyAsync(TicketMutationActor actor, Guid ticketId, long replyId, string? bodyHtml, CancellationToken ct);
    Task DeleteReplyAsync(TicketMutationActor actor, Guid ticketId, long replyId, CancellationToken ct);
    /// <paramref name="eventId"/> xor <paramref name="replyId"/>.
    Task SetLikeAsync(TicketMutationActor actor, Guid ticketId, long? eventId, long? replyId, bool liked, CancellationToken ct);
}

/// v0.1.54 — replies and likes on internal notes and internal calls.
/// Access = the ticket's queue access (shared mutation precheck); only an
/// internal Note / internal Call is a valid root. Replies nest to any depth
/// (adjacency list + root id). A reply notifies the author of what it answers,
/// a like the author of what is liked — never oneself — in the personal Tags
/// bell; mail follows each recipient's profile switch. @@-tags inside a reply
/// go through the regular mention service (personal and group tags).
public sealed class NoteThreadService : INoteThreadService
{
    public const int MaxTextLength = 4000;
    public const int MaxMentions = 20;
    public const string ReplyType = "note_reply";
    public const string LikeType = "note_like";

    private readonly INoteThreadRepository _repo;
    private readonly ITicketMutationService _mutations;
    private readonly ITicketHtmlSanitizer _sanitizer;
    private readonly IUserService _users;
    private readonly INotificationRepository _notifications;
    private readonly IUserNotifier _pusher;
    private readonly INotificationMailer _mailer;
    private readonly IMentionNotificationService _mentions;
    private readonly ITicketListNotifier _tickets;
    private readonly IAuditLogger _audit;
    private readonly ILogger<NoteThreadService> _logger;

    public NoteThreadService(
        INoteThreadRepository repo, ITicketMutationService mutations, ITicketHtmlSanitizer sanitizer,
        IUserService users, INotificationRepository notifications, IUserNotifier pusher,
        INotificationMailer mailer, IMentionNotificationService mentions, ITicketListNotifier tickets,
        IAuditLogger audit, ILogger<NoteThreadService> logger)
    {
        _repo = repo;
        _mutations = mutations;
        _sanitizer = sanitizer;
        _users = users;
        _notifications = notifications;
        _pusher = pusher;
        _mailer = mailer;
        _mentions = mentions;
        _tickets = tickets;
        _audit = audit;
        _logger = logger;
    }

    private static bool IsAdmin(TicketMutationActor actor)
        => string.Equals(actor.Role, "Admin", StringComparison.OrdinalIgnoreCase);

    private static NoteThreadRejectedException NotFound() => new(NoteThreadRejectCode.NotFound, "Not found.");

    private async Task<Domain.Tickets.Ticket> RequireTicketAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var access = await _mutations.PrecheckAccessAsync(actor, ticketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null) throw NotFound();
        return access.Ticket;
    }

    public async Task<NoteThreadData> GetAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        return await _repo.GetForTicketAsync(ticketId, ct);
    }

    public async Task<NoteReply> AddReplyAsync(TicketMutationActor actor, Guid ticketId, long eventId, NoteReplyInput input, CancellationToken ct)
    {
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        var target = await _repo.GetTargetAsync(ticketId, eventId, ct) ?? throw NotFound();

        NoteReply? parent = null;
        if (input.ParentReplyId is { } pid)
        {
            parent = await _repo.GetReplyAsync(ticketId, pid, ct);
            if (parent is null || parent.RootEventId != eventId)
                throw new NoteThreadRejectedException(NoteThreadRejectCode.Invalid, "That reply is not part of this note's thread.");
            if (parent.DeletedUtc is not null)
                throw new NoteThreadRejectedException(NoteThreadRejectCode.Invalid, "You can't reply to a deleted reply.");
        }

        var (html, text) = Clean(input.BodyHtml);
        var mentioned = await _users.FilterAgentIdsAsync(
            (input.MentionedUserIds ?? Array.Empty<Guid>()).Distinct().Take(MaxMentions).ToList(), ct);
        var groups = (input.MentionedGroupIds ?? Array.Empty<Guid>()).Where(g => g != Guid.Empty).Distinct().Take(MaxMentions).ToList();

        var id = await _repo.CreateReplyAsync(eventId, parent?.Id, (parent?.Depth ?? 0) + 1, actor.UserId, html, text,
            new { mentionedUserIds = mentioned, mentionedGroupIds = groups }, ct);
        var reply = await _repo.GetReplyAsync(ticketId, id, ct) ?? throw NotFound();

        await AuditAsync(actor, "ticket.note.reply.created", ticketId,
            new { eventId, replyId = id, parentReplyId = parent?.Id, depth = reply.Depth, mentionedUserCount = mentioned.Count }, ct);
        await PushAsync(ticketId, ct);

        // Notify the author of what this reply answers — unless that is the
        // replier, or they are @@-tagged in it (the tag already notifies them).
        var recipient = parent is not null ? parent.AuthorUserId : target.AuthorUserId;
        if (recipient is { } r && r != actor.UserId && !mentioned.Contains(r))
        {
            await NotifyAsync(ReplyType, r, actor, ticket, eventId, id, text,
                parent is null ? "replied to your note." : "replied to your reply.",
                NotificationPreferenceKeys.ReplyEmailPreference, "Reply", "New reply", ct);
        }

        if (mentioned.Count > 0 || groups.Count > 0)
        {
            var actorUser = await _users.FindByIdAsync(actor.UserId, ct);
            await _mentions.PublishAsync(new MentionNotificationSource(
                TicketId: ticketId, TicketNumber: ticket.Number, TicketSubject: ticket.Subject, QueueId: ticket.QueueId,
                EventId: eventId, EventType: "NoteReply", SourceUserId: actor.UserId,
                SourceUserEmail: actorUser?.Email ?? string.Empty, MentionedUserIds: mentioned,
                BodyHtml: html, BodyText: text, MentionedMailboxIds: Array.Empty<Guid>(),
                MentionedGroupIds: groups), ct);
        }
        return reply;
    }

    public async Task<NoteReply> EditReplyAsync(TicketMutationActor actor, Guid ticketId, long replyId, string? bodyHtml, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        var reply = await _repo.GetReplyAsync(ticketId, replyId, ct) ?? throw NotFound();
        if (reply.DeletedUtc is not null) throw NotFound();
        if (reply.AuthorUserId != actor.UserId)
            throw new NoteThreadRejectedException(NoteThreadRejectCode.Forbidden, "You can only edit your own replies.");
        var (html, text) = Clean(bodyHtml);
        if (!await _repo.UpdateReplyAsync(replyId, html, text, ct)) throw NotFound();
        await AuditAsync(actor, "ticket.note.reply.edited", ticketId, new { replyId, eventId = reply.RootEventId }, ct);
        await PushAsync(ticketId, ct);
        return await _repo.GetReplyAsync(ticketId, replyId, ct) ?? throw NotFound();
    }

    public async Task DeleteReplyAsync(TicketMutationActor actor, Guid ticketId, long replyId, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        var reply = await _repo.GetReplyAsync(ticketId, replyId, ct) ?? throw NotFound();
        if (reply.DeletedUtc is not null) return; // idempotent
        if (reply.AuthorUserId != actor.UserId && !IsAdmin(actor))
            throw new NoteThreadRejectedException(NoteThreadRejectCode.Forbidden, "You can only delete your own replies.");
        await _repo.SoftDeleteReplyAsync(replyId, actor.UserId, ct);
        await AuditAsync(actor, "ticket.note.reply.deleted", ticketId,
            new { replyId, eventId = reply.RootEventId, byAdmin = reply.AuthorUserId != actor.UserId }, ct);
        await PushAsync(ticketId, ct);
    }

    public async Task SetLikeAsync(TicketMutationActor actor, Guid ticketId, long? eventId, long? replyId, bool liked, CancellationToken ct)
    {
        if ((eventId is null) == (replyId is null))
            throw new NoteThreadRejectedException(NoteThreadRejectCode.Invalid, "Like a note or a reply.");
        var ticket = await RequireTicketAsync(actor, ticketId, ct);

        long rootEventId;
        Guid? author;
        string preview;
        if (eventId is { } eid)
        {
            var target = await _repo.GetTargetAsync(ticketId, eid, ct) ?? throw NotFound();
            (rootEventId, author, preview) = (eid, target.AuthorUserId, target.BodyText);
        }
        else
        {
            var reply = await _repo.GetReplyAsync(ticketId, replyId!.Value, ct) ?? throw NotFound();
            if (reply.DeletedUtc is not null) throw NotFound();
            (rootEventId, author, preview) = (reply.RootEventId, reply.AuthorUserId, reply.BodyText);
        }

        if (!liked)
        {
            if (await _repo.UnlikeAsync(eventId, replyId, actor.UserId, ct)) await PushAsync(ticketId, ct);
            return;
        }

        if (!await _repo.LikeAsync(eventId, replyId, actor.UserId, ct)) return; // already liked
        await PushAsync(ticketId, ct);

        // One like notification per (liker, target): re-liking after an
        // unlike stays quiet. Never for liking one's own note.
        if (author is { } a && a != actor.UserId
            && !await _repo.LikeNotificationExistsAsync(actor.UserId, rootEventId, replyId, ct))
        {
            await NotifyAsync(LikeType, a, actor, ticket, rootEventId, replyId, preview,
                replyId is null ? "liked your note." : "liked your reply.",
                NotificationPreferenceKeys.LikeEmailPreference, "Liked", "New like", ct);
        }
    }

    /// Bell row + live toast + (profile-governed) mail for one recipient.
    /// Best effort: the reply/like itself is already saved.
    private async Task NotifyAsync(
        string type, Guid recipient, TicketMutationActor actor, Domain.Tickets.Ticket ticket,
        long eventId, long? replyId, string text, string action, NotificationMailPreference pref,
        string subjectPrefix, string eyebrow, CancellationToken ct)
    {
        try
        {
            if ((await _users.FilterAgentIdsAsync(new[] { recipient }, ct)).Count == 0) return;
            var preview = Preview(text);
            var rows = await _notifications.CreateManyAsync(new[]
            {
                new NewUserNotification(recipient, actor.UserId, type, ticket.Id, ticket.Number, ticket.Subject,
                    eventId, type == ReplyType ? "NoteReply" : "NoteLike", preview, replyId),
            }, ct);
            foreach (var row in rows)
            {
                try
                {
                    await _pusher.NotifyMentionAsync(row.UserId, new UserNotificationPush(
                        row.Id, row.TicketId, row.TicketNumber, row.TicketSubject, row.SourceUserEmail,
                        row.EventId, row.EventType, row.PreviewText, row.CreatedUtc, NotificationType: type,
                        ReplyId: row.ReplyId), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Push failed for notification {NotificationId}.", row.Id);
                }
            }
            var anchor = replyId is { } rid ? $"reply-{rid}" : $"event-{eventId}";
            await _mailer.SendForRowsAsync(rows, new NotificationMailContent(
                QueueId: ticket.QueueId, TicketId: ticket.Id, TicketNumber: ticket.Number, TicketSubject: ticket.Subject,
                SubjectPrefix: subjectPrefix, Eyebrow: eyebrow, ActorEmail: rows.FirstOrDefault()?.SourceUserEmail ?? string.Empty,
                ActionText: action, Preview: preview, LinkPath: $"/tickets/{ticket.Id}#{anchor}"), pref, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Note {Type} notification failed for ticket {TicketId}.", type, ticket.Id);
        }
    }

    private (string Html, string Text) Clean(string? bodyHtml)
    {
        var html = (_sanitizer.Sanitize(bodyHtml ?? string.Empty) ?? string.Empty).Trim();
        var text = KbBodyStripper.HtmlToText(html).Trim();
        if (text.Length == 0)
            throw new NoteThreadRejectedException(NoteThreadRejectCode.Invalid, "A reply can't be empty.");
        if (text.Length > MaxTextLength)
            throw new NoteThreadRejectedException(NoteThreadRejectCode.Invalid, $"A reply can be at most {MaxTextLength} characters.");
        return (html, text);
    }

    private static string Preview(string text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= 200 ? t : t[..199] + "…";
    }

    private async Task PushAsync(Guid ticketId, CancellationToken ct)
    {
        try { await _tickets.NotifyUpdatedAsync(ticketId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Ticket refresh push failed for {TicketId}.", ticketId); }
    }

    private Task AuditAsync(TicketMutationActor actor, string type, Guid ticketId, object payload, CancellationToken ct)
        => _audit.LogAsync(new AuditEvent(
            EventType: type, Actor: actor.AuditActor, ActorRole: actor.AuditRole, Target: ticketId.ToString(),
            ClientIp: actor.ClientIp, UserAgent: actor.UserAgent, Payload: payload), ct);
}
