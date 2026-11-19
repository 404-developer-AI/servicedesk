using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Tickets.Following;

public sealed record FollowedTicket(
    Guid TicketId, long TicketNumber, string Subject,
    string StatusName, string StatusColor, string StatusCategory,
    DateTime FollowedUtc, DateTime LastSeenUtc, DateTime LastActivityUtc, bool HasActivity,
    string? Note = null);

public enum FollowResult { Followed, AlreadyFollowing, LimitReached }

public interface ITicketFollowRepository
{
    /// Follows the ticket unless the user already follows <paramref name="maxPerUser"/> tickets.
    Task<FollowResult> FollowAsync(Guid userId, Guid ticketId, int maxPerUser, CancellationToken ct);
    Task<bool> UnfollowAsync(Guid userId, Guid ticketId, CancellationToken ct);
    /// Stamps last_seen_utc = now() (server time). False when not followed.
    Task<bool> MarkSeenAsync(Guid userId, Guid ticketId, CancellationToken ct);
    /// Sets (or clears, with null) the user's own note. False when not followed.
    Task<bool> SetNoteAsync(Guid userId, Guid ticketId, string? note, CancellationToken ct);
    /// The followed tickets the user can still see under <paramref name="scope"/>,
    /// tickets with news first, then the most recent activity first.
    Task<IReadOnlyList<FollowedTicket>> ListAsync(Guid userId, QueueAccessScope scope, CancellationToken ct);
}

/// v0.1.54 — following tickets. Nothing is written when a ticket changes:
/// "has news" is derived when the list is read, as the latest of the
/// ticket's updated_utc, its newest event, its newest note reply (created,
/// edited or deleted) and its newest like, compared with last_seen_utc.
/// That way every write path — endpoints, triggers, workers, mail polling —
/// counts without a hook in each. The LATERAL only runs over the user's
/// followed tickets (capped by a setting) and uses the ticket_events
/// (ticket_id, created_utc) and ticket_note_replies root indexes.
public sealed class TicketFollowRepository : ITicketFollowRepository
{
    private readonly NpgsqlDataSource _ds;

    public TicketFollowRepository(NpgsqlDataSource ds) => _ds = ds;

    public async Task<FollowResult> FollowAsync(Guid userId, Guid ticketId, int maxPerUser, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_follows (user_id, ticket_id)
            SELECT @userId, @ticketId
            WHERE (SELECT count(*) FROM ticket_follows WHERE user_id = @userId) < @maxPerUser
            ON CONFLICT (user_id, ticket_id) DO NOTHING
            """, new { userId, ticketId, maxPerUser }, cancellationToken: ct));
        if (inserted == 1) return FollowResult.Followed;
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM ticket_follows WHERE user_id = @userId AND ticket_id = @ticketId)",
            new { userId, ticketId }, cancellationToken: ct));
        return exists ? FollowResult.AlreadyFollowing : FollowResult.LimitReached;
    }

    public async Task<bool> UnfollowAsync(Guid userId, Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ticket_follows WHERE user_id = @userId AND ticket_id = @ticketId",
            new { userId, ticketId }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> MarkSeenAsync(Guid userId, Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_follows SET last_seen_utc = now() WHERE user_id = @userId AND ticket_id = @ticketId",
            new { userId, ticketId }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> SetNoteAsync(Guid userId, Guid ticketId, string? note, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_follows SET note = @note WHERE user_id = @userId AND ticket_id = @ticketId",
            new { userId, ticketId, note }, cancellationToken: ct)) == 1;
    }

    public async Task<IReadOnlyList<FollowedTicket>> ListAsync(Guid userId, QueueAccessScope scope, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql = $"""
            SELECT t.id AS TicketId, t.number AS TicketNumber, t.subject AS Subject,
                   s.name AS StatusName, s.color AS StatusColor, s.state_category AS StatusCategory,
                   f.followed_utc AS FollowedUtc, f.last_seen_utc AS LastSeenUtc,
                   a.last_activity AS LastActivityUtc,
                   (a.last_activity > f.last_seen_utc) AS HasActivity,
                   f.note AS Note
            FROM ticket_follows f
            JOIN tickets t ON t.id = f.ticket_id AND NOT t.is_deleted
            JOIN statuses s ON s.id = t.status_id
            CROSS JOIN LATERAL (
                SELECT GREATEST(
                    t.updated_utc,
                    (SELECT max(e.created_utc) FROM ticket_events e WHERE e.ticket_id = t.id),
                    (SELECT max(GREATEST(r.created_utc, r.edited_utc, r.deleted_utc))
                       FROM ticket_events re
                       JOIN ticket_note_replies r ON r.root_event_id = re.id
                      WHERE re.ticket_id = t.id),
                    (SELECT max(l.created_utc)
                       FROM ticket_note_likes l
                       LEFT JOIN ticket_note_replies lr ON lr.id = l.reply_id
                       JOIN ticket_events le ON le.id = COALESCE(l.event_id, lr.root_event_id)
                      WHERE le.ticket_id = t.id)
                ) AS last_activity
            ) a
            WHERE f.user_id = @userId AND {scope.SqlPredicate("t.queue_id")}
            ORDER BY HasActivity DESC, LastActivityUtc DESC, t.number DESC
            """;
        var rows = await conn.QueryAsync<FollowedTicket>(new CommandDefinition(
            sql, scope.Parameters(new { userId }), cancellationToken: ct));
        return rows.ToList();
    }
}
