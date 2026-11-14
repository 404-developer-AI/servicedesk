using System.Text.Json;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Tickets.NoteThreads;

/// The internal note / internal call a thread hangs under.
public sealed record NoteThreadTarget(long EventId, Guid TicketId, Guid? AuthorUserId, string EventType, string BodyText);

public sealed record NoteReply(
    long Id, long RootEventId, long? ParentReplyId, int Depth, Guid? AuthorUserId, string? AuthorEmail,
    string BodyHtml, string BodyText, DateTime CreatedUtc, DateTime? EditedUtc, DateTime? DeletedUtc);

public sealed record NoteLike(long? EventId, long? ReplyId, Guid UserId, string Email);

public sealed record NoteThreadData(IReadOnlyList<NoteReply> Replies, IReadOnlyList<NoteLike> Likes);

public interface INoteThreadRepository
{
    /// The event, only when it is on <paramref name="ticketId"/> and is an
    /// internal Note or an internal Call — anything else is not a valid target.
    Task<NoteThreadTarget?> GetTargetAsync(Guid ticketId, long eventId, CancellationToken ct);
    /// The reply, only when its root event is on <paramref name="ticketId"/>.
    Task<NoteReply?> GetReplyAsync(Guid ticketId, long replyId, CancellationToken ct);
    Task<long> CreateReplyAsync(long rootEventId, long? parentReplyId, int depth, Guid authorUserId,
        string bodyHtml, string bodyText, object metadata, CancellationToken ct);
    Task<bool> UpdateReplyAsync(long replyId, string bodyHtml, string bodyText, CancellationToken ct);
    /// Soft delete: the body is cleared, the row stays so deeper replies keep their parent.
    Task<bool> SoftDeleteReplyAsync(long replyId, Guid byUserId, CancellationToken ct);
    /// Every reply (any depth) and like of the ticket's notes — two indexed reads.
    Task<NoteThreadData> GetForTicketAsync(Guid ticketId, CancellationToken ct);
    /// True when this call created the like (false = it already existed).
    Task<bool> LikeAsync(long? eventId, long? replyId, Guid userId, CancellationToken ct);
    Task<bool> UnlikeAsync(long? eventId, long? replyId, Guid userId, CancellationToken ct);
    /// Whether <paramref name="sourceUserId"/> already caused a like
    /// notification for this target — re-liking after an unlike stays quiet.
    Task<bool> LikeNotificationExistsAsync(Guid sourceUserId, long eventId, long? replyId, CancellationToken ct);
}

/// v0.1.54 — Dapper repository for replies and likes on internal notes.
/// Storage = adjacency list + root (see the schema comment in DatabaseBootstrapper).
public sealed class NoteThreadRepository : INoteThreadRepository
{
    private readonly NpgsqlDataSource _ds;

    public NoteThreadRepository(NpgsqlDataSource ds) => _ds = ds;

    /// The one rule for what a thread can hang under.
    public const string TargetPredicate = "(e.event_type = 'Note' OR (e.event_type = 'Call' AND e.is_internal = TRUE))";

    private const string ReplySelect = """
        SELECT r.id AS Id, r.root_event_id AS RootEventId, r.parent_reply_id AS ParentReplyId, r.depth::int AS Depth,
               r.author_user_id AS AuthorUserId, u.email::text AS AuthorEmail,
               r.body_html AS BodyHtml, r.body_text AS BodyText,
               r.created_utc AS CreatedUtc, r.edited_utc AS EditedUtc, r.deleted_utc AS DeletedUtc
        FROM ticket_note_replies r
        JOIN ticket_events e ON e.id = r.root_event_id
        LEFT JOIN users u ON u.id = r.author_user_id
        """;

    public async Task<NoteThreadTarget?> GetTargetAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<NoteThreadTarget>(new CommandDefinition($"""
            SELECT e.id AS EventId, e.ticket_id AS TicketId, e.author_user_id AS AuthorUserId,
                   e.event_type AS EventType, COALESCE(e.body_text, '') AS BodyText
            FROM ticket_events e
            WHERE e.id = @eventId AND e.ticket_id = @ticketId AND {TargetPredicate}
            """, new { ticketId, eventId }, cancellationToken: ct));
    }

    public async Task<NoteReply?> GetReplyAsync(Guid ticketId, long replyId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<NoteReply>(new CommandDefinition(
            ReplySelect + " WHERE r.id = @replyId AND e.ticket_id = @ticketId",
            new { ticketId, replyId }, cancellationToken: ct));
    }

    public async Task<long> CreateReplyAsync(long rootEventId, long? parentReplyId, int depth, Guid authorUserId,
        string bodyHtml, string bodyText, object metadata, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO ticket_note_replies (root_event_id, parent_reply_id, depth, author_user_id, body_html, body_text, metadata)
            VALUES (@rootEventId, @parentReplyId, @depth, @authorUserId, @bodyHtml, @bodyText, @metadata::jsonb)
            RETURNING id
            """, new
        {
            rootEventId, parentReplyId, depth = (short)depth, authorUserId, bodyHtml, bodyText,
            metadata = JsonSerializer.Serialize(metadata),
        }, cancellationToken: ct));
    }

    public async Task<bool> UpdateReplyAsync(long replyId, string bodyHtml, string bodyText, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_note_replies SET body_html = @bodyHtml, body_text = @bodyText, edited_utc = now()
            WHERE id = @replyId AND deleted_utc IS NULL
            """, new { replyId, bodyHtml, bodyText }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> SoftDeleteReplyAsync(long replyId, Guid byUserId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_note_replies
               SET body_html = '', body_text = '', deleted_utc = now(), deleted_by_user_id = @byUserId
             WHERE id = @replyId AND deleted_utc IS NULL
            """, new { replyId, byUserId }, cancellationToken: ct)) > 0;
    }

    public async Task<NoteThreadData> GetForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var replies = (await conn.QueryAsync<NoteReply>(new CommandDefinition(
            ReplySelect + " WHERE e.ticket_id = @ticketId ORDER BY r.root_event_id, r.id",
            new { ticketId }, cancellationToken: ct))).ToList();
        var likes = (await conn.QueryAsync<NoteLike>(new CommandDefinition("""
            SELECT l.event_id AS EventId, l.reply_id AS ReplyId, l.user_id AS UserId, u.email::text AS Email
            FROM ticket_note_likes l
            JOIN users u ON u.id = l.user_id
            LEFT JOIN ticket_note_replies r ON r.id = l.reply_id
            JOIN ticket_events e ON e.id = COALESCE(l.event_id, r.root_event_id)
            WHERE e.ticket_id = @ticketId
            ORDER BY l.created_utc, l.id
            """, new { ticketId }, cancellationToken: ct))).ToList();
        return new NoteThreadData(replies, likes);
    }

    public async Task<bool> LikeAsync(long? eventId, long? replyId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql = eventId is not null
            ? """
              INSERT INTO ticket_note_likes (event_id, user_id) VALUES (@eventId, @userId)
              ON CONFLICT (event_id, user_id) WHERE event_id IS NOT NULL DO NOTHING
              """
            : """
              INSERT INTO ticket_note_likes (reply_id, user_id) VALUES (@replyId, @userId)
              ON CONFLICT (reply_id, user_id) WHERE reply_id IS NOT NULL DO NOTHING
              """;
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { eventId, replyId, userId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> UnlikeAsync(long? eventId, long? replyId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql = eventId is not null
            ? "DELETE FROM ticket_note_likes WHERE event_id = @eventId AND user_id = @userId"
            : "DELETE FROM ticket_note_likes WHERE reply_id = @replyId AND user_id = @userId";
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { eventId, replyId, userId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> LikeNotificationExistsAsync(Guid sourceUserId, long eventId, long? replyId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM user_notifications
                WHERE notification_type = 'note_like' AND source_user_id = @sourceUserId
                  AND event_id = @eventId AND reply_id IS NOT DISTINCT FROM @replyId)
            """, new { sourceUserId, eventId, replyId }, cancellationToken: ct));
    }
}
