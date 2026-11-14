using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Tickets.NoteThreads;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// v0.1.54 — replies and likes on internal notes against a real database:
/// trees of any depth come back in one read, only internal notes/calls are
/// targets, deletes are soft (children survive), likes are idempotent, and
/// the notification rules (never yourself, tag wins over reply, re-like is
/// quiet, mail follows the profile defaults) hold.
[Collection(PostgresCollection.Name)]
public sealed class NoteThreadDbTests
{
    private readonly PostgresDatabase _db;

    public NoteThreadDbTests(PostgresDatabase db) => _db = db;

    private sealed record World(
        NoteThreadService Service, Guid TicketId, long NoteId, Guid NoteAuthor, Guid Replier,
        RecordingMailer Mailer, RecordingMentions Mentions);

    private async Task<World> WorldAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Licence request", await seed.ContactAsync(), q, s, p);
        var author = await seed.UserAsync("Agent");
        var replier = await seed.UserAsync("Agent");
        var noteId = await seed.EventAsync(ticketId, "Note", isInternal: true, "Please check the licences", author);

        var mutations = new AccessAll(ticketId, q);
        var mailer = new RecordingMailer();
        var mentions = new RecordingMentions();
        var svc = new NoteThreadService(
            new NoteThreadRepository(_db.DataSource), mutations, new TicketHtmlSanitizer(),
            new UserService(_db.DataSource), new NotificationRepository(_db.DataSource), new NullUserNotifier(),
            mailer, mentions, new NullTicketListNotifier(), new NullAudit(), NullLogger<NoteThreadService>.Instance);
        return new World(svc, ticketId, noteId, author, replier, mailer, mentions);
    }

    private static TicketMutationActor Actor(Guid id, string role = "Agent")
        => new(id, role, id.ToString(), role, null, null);

    private static NoteReplyInput Reply(string text, long? parent = null, IReadOnlyList<Guid>? mentions = null)
        => new(parent, $"<p>{text}</p>", mentions, null);

    private async Task<List<(string Type, Guid UserId, long? ReplyId)>> NotificationsAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<(string, Guid, long?)>(
            "SELECT notification_type, user_id, reply_id FROM user_notifications WHERE ticket_id = @ticketId ORDER BY created_utc, id",
            new { ticketId })).ToList();
    }

    [PostgresFact]
    public async Task A_three_level_thread_comes_back_in_one_read_and_notifies_whoever_was_answered()
    {
        var w = await WorldAsync();
        var r1 = await w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, w.NoteId, Reply("On it"), default);
        var r2 = await w.Service.AddReplyAsync(Actor(w.NoteAuthor), w.TicketId, w.NoteId, Reply("Thanks", r1.Id), default);
        var r3 = await w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, w.NoteId, Reply("Done now", r2.Id), default);

        Assert.Equal(new[] { 1, 2, 3 }, new[] { r1.Depth, r2.Depth, r3.Depth });
        var data = await w.Service.GetAsync(Actor(w.Replier), w.TicketId, default);
        Assert.Equal(new long?[] { null, r1.Id, r2.Id }, data.Replies.Select(r => r.ParentReplyId));

        // r1 → note author, r2 → r1's author, r3 → r2's author.
        var n = await NotificationsAsync(w.TicketId);
        Assert.Equal(new[] { (NoteThreadService.ReplyType, w.NoteAuthor, (long?)r1.Id),
                             (NoteThreadService.ReplyType, w.Replier, (long?)r2.Id),
                             (NoteThreadService.ReplyType, w.NoteAuthor, (long?)r3.Id) }, n);
        Assert.All(w.Mailer.Sent, m => Assert.Equal(NotificationPreferenceKeys.ReplyEmail, m.Key));
    }

    [PostgresFact]
    public async Task Replying_to_your_own_note_notifies_nobody_and_a_tag_wins_over_the_reply_notice()
    {
        var w = await WorldAsync();
        await w.Service.AddReplyAsync(Actor(w.NoteAuthor), w.TicketId, w.NoteId, Reply("Note to self"), default);
        Assert.Empty(await NotificationsAsync(w.TicketId));

        await w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, w.NoteId, Reply("@author look", mentions: new[] { w.NoteAuthor }), default);
        Assert.Empty(await NotificationsAsync(w.TicketId));                  // no separate reply notice…
        Assert.Equal(new[] { w.NoteAuthor }, Assert.Single(w.Mentions.Sources).MentionedUserIds); // …the tag covers it
    }

    [PostgresFact]
    public async Task Only_internal_notes_and_calls_are_targets_and_parents_must_share_the_root()
    {
        var w = await WorldAsync();
        var seed = new DbSeed(_db.DataSource);
        var publicReply = await seed.EventAsync(w.TicketId, "Comment", isInternal: false);
        var publicCall = await seed.EventAsync(w.TicketId, "Call", isInternal: false);
        var internalCall = await seed.EventAsync(w.TicketId, "Call", isInternal: true, authorUserId: w.NoteAuthor);

        foreach (var bad in new[] { publicReply, publicCall })
        {
            var ex = await Assert.ThrowsAsync<NoteThreadRejectedException>(() =>
                w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, bad, Reply("x"), default));
            Assert.Equal(NoteThreadRejectCode.NotFound, ex.Code);
        }
        var onCall = await w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, internalCall, Reply("Called back"), default);

        var cross = await Assert.ThrowsAsync<NoteThreadRejectedException>(() =>
            w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, w.NoteId, Reply("x", onCall.Id), default));
        Assert.Equal(NoteThreadRejectCode.Invalid, cross.Code);

        var empty = await Assert.ThrowsAsync<NoteThreadRejectedException>(() =>
            w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, w.NoteId, new NoteReplyInput(null, "<p> </p>", null, null), default));
        Assert.Equal(NoteThreadRejectCode.Invalid, empty.Code);
    }

    [PostgresFact]
    public async Task Deleting_is_soft_keeps_children_and_only_author_or_admin_may()
    {
        var w = await WorldAsync();
        var parent = await w.Service.AddReplyAsync(Actor(w.Replier), w.TicketId, w.NoteId, Reply("Parent"), default);
        var child = await w.Service.AddReplyAsync(Actor(w.NoteAuthor), w.TicketId, w.NoteId, Reply("Child", parent.Id), default);

        var forbidden = await Assert.ThrowsAsync<NoteThreadRejectedException>(() =>
            w.Service.DeleteReplyAsync(Actor(w.NoteAuthor), w.TicketId, parent.Id, default));
        Assert.Equal(NoteThreadRejectCode.Forbidden, forbidden.Code);
        await Assert.ThrowsAsync<NoteThreadRejectedException>(() =>
            w.Service.EditReplyAsync(Actor(w.NoteAuthor), w.TicketId, parent.Id, "<p>hijack</p>", default));

        var admin = await new DbSeed(_db.DataSource).UserAsync("Admin");
        await w.Service.DeleteReplyAsync(Actor(admin, "Admin"), w.TicketId, parent.Id, default);

        var data = await w.Service.GetAsync(Actor(w.Replier), w.TicketId, default);
        var deleted = Assert.Single(data.Replies, r => r.Id == parent.Id);
        Assert.NotNull(deleted.DeletedUtc);
        Assert.Equal(string.Empty, deleted.BodyText);
        Assert.Contains(data.Replies, r => r.Id == child.Id && r.ParentReplyId == parent.Id);
    }

    [PostgresFact]
    public async Task Likes_are_idempotent_never_self_notified_and_a_re_like_stays_quiet()
    {
        var w = await WorldAsync();
        var me = Actor(w.Replier);
        await Task.WhenAll(
            w.Service.SetLikeAsync(me, w.TicketId, w.NoteId, null, true, default),
            w.Service.SetLikeAsync(me, w.TicketId, w.NoteId, null, true, default));
        var data = await w.Service.GetAsync(me, w.TicketId, default);
        Assert.Single(data.Likes, l => l.EventId == w.NoteId && l.UserId == w.Replier);

        await w.Service.SetLikeAsync(me, w.TicketId, w.NoteId, null, false, default);
        await w.Service.SetLikeAsync(me, w.TicketId, w.NoteId, null, true, default);
        Assert.Single(await NotificationsAsync(w.TicketId), n => n.Type == NoteThreadService.LikeType);
        Assert.All(w.Mailer.Sent, m => Assert.Equal(NotificationPreferenceKeys.LikeEmail, m.Key));

        await w.Service.SetLikeAsync(Actor(w.NoteAuthor), w.TicketId, w.NoteId, null, true, default); // own note
        Assert.Single(await NotificationsAsync(w.TicketId), n => n.Type == NoteThreadService.LikeType);
    }

    // ---- fakes ------------------------------------------------------------

    private sealed class AccessAll(Guid ticketId, Guid queueId) : ITicketMutationService
    {
        private readonly Ticket _ticket = new(ticketId, 1, "Licence request", Guid.NewGuid(), null, queueId,
            Guid.NewGuid(), Guid.NewGuid(), null, "web", null, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null, false);
        public Task<AccessPrecheck> PrecheckAccessAsync(TicketMutationActor actor, Guid id, CancellationToken ct)
            => Task.FromResult(id == ticketId ? new AccessPrecheck(TicketMutationCheck.Ok, _ticket) : new AccessPrecheck(TicketMutationCheck.NotFound, null));
        public Task<FieldUpdatePrecheck> PrecheckFieldUpdateAsync(TicketMutationActor actor, Guid id, TicketFieldUpdate update, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> ApplyFieldUpdateAsync(Guid id, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task PublishFieldUpdateAsync(TicketMutationActor actor, Guid id, TicketFieldUpdate update, object auditPayload, TriggerChangeSet? changeSet, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> AddEventAsync(Guid id, NewTicketEvent input, CancellationToken ct) => throw new NotImplementedException();
        public Task PublishEventAsync(TicketMutationActor actor, Guid id, TicketEvent evt, object auditPayload, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class RecordingMailer : INotificationMailer
    {
        public List<NotificationMailPreference> Sent { get; } = new();
        public Task SendForRowsAsync(IReadOnlyList<UserNotificationRow> rows, NotificationMailContent content, NotificationMailPreference preference, CancellationToken ct)
        {
            Sent.Add(preference);
            return Task.CompletedTask;
        }
        public Task SendToAddressesAsync(IReadOnlyList<string> addresses, NotificationMailContent content, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingMentions : IMentionNotificationService
    {
        public List<MentionNotificationSource> Sources { get; } = new();
        public Task PublishAsync(MentionNotificationSource source, CancellationToken ct)
        {
            Sources.Add(source);
            return Task.CompletedTask;
        }
    }

    private sealed class NullAudit : IAuditLogger
    {
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
