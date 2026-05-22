using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// Per-user "recent tickets" sidebar list, persisted server-side so the
/// same user sees the same list across browsers and devices. Lives next
/// to the ticket persistence layer because every read enriches the
/// stored ticket-ids with subject + status snapshot via a single JOIN.
public interface IRecentTicketsService
{
    /// Returns the user's recent-list ordered by position ascending.
    /// Enriched with subject + status snapshot in a single query so the
    /// sidebar never N+1s.
    Task<IReadOnlyList<RecentTicketEntry>> ListForUserAsync(Guid userId, CancellationToken ct = default);

    /// Idempotent upsert. New tickets land at the tail (max position + 1)
    /// and the oldest entries are pruned when the user exceeds the cap.
    /// Existing entries keep their position — the sidebar's manual order
    /// survives a re-view. Returns true when the list actually changed
    /// (caller decides whether to broadcast).
    Task<bool> AddAsync(Guid userId, Guid ticketId, CancellationToken ct = default);

    /// Removes one ticket from the user's list. Returns true when a row
    /// was actually deleted (caller decides whether to broadcast).
    Task<bool> RemoveAsync(Guid userId, Guid ticketId, CancellationToken ct = default);

    /// Replaces the user's full ordering with the supplied id list.
    /// Unknown ids (no row in user_recent_tickets) are silently dropped;
    /// missing ids retain their old position. Used by the drag-reorder
    /// flow which only sends ids the client already has.
    Task ReorderAsync(Guid userId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);
}

/// One row in the user's recent list. Status snapshot may be null when
/// the joined status row is missing (e.g. a status was deleted while
/// the ticket survives — rare, but the sidebar still needs to render).
public sealed class RecentTicketEntry
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public string Subject { get; set; } = "";
    public string? StatusColor { get; set; }
    public string? StatusName { get; set; }
    public string? StatusStateCategory { get; set; }
}

public sealed class RecentTicketsService : IRecentTicketsService
{
    /// Hard cap on the number of entries per user. Matches the previous
    /// localStorage limit (50) so existing users do not feel a regression.
    /// Exceeding the cap drops the oldest (lowest position) on insert.
    public const int MaxEntriesPerUser = 50;

    private readonly NpgsqlDataSource _dataSource;

    public RecentTicketsService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<RecentTicketEntry>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // LEFT JOIN on statuses so a soft-deleted status doesn't drop the
        // ticket from the sidebar — fields just come back null.
        const string sql = """
            SELECT
                t.id                  AS Id,
                t.number              AS Number,
                t.subject             AS Subject,
                s.color               AS StatusColor,
                s.name                AS StatusName,
                s.state_category      AS StatusStateCategory
            FROM user_recent_tickets r
            JOIN tickets  t ON t.id = r.ticket_id
            LEFT JOIN statuses s ON s.id = t.status_id
            WHERE r.user_id = @UserId
            ORDER BY r.position ASC, r.added_utc ASC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RecentTicketEntry>(
            new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> AddAsync(Guid userId, Guid ticketId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Sanity check: ticket must exist. Cheap row-lock keeps a
        // concurrent ticket-delete from racing the insert.
        var ticketExists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM tickets WHERE id = @id)",
                new { id = ticketId },
                tx,
                cancellationToken: ct));
        if (!ticketExists)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // Already in the list? Don't touch position — the user's manual
        // ordering survives a re-view. Touch added_utc so a future LRU
        // policy has a recency signal (today only used as a tie-break).
        var existed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE user_recent_tickets
                SET added_utc = now()
                WHERE user_id = @UserId AND ticket_id = @TicketId
            """,
            new { UserId = userId, TicketId = ticketId },
            tx,
            cancellationToken: ct));
        if (existed > 0)
        {
            await tx.CommitAsync(ct);
            // No structural change — caller skips the broadcast.
            return false;
        }

        var nextPosition = (await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                "SELECT MAX(position) + 1 FROM user_recent_tickets WHERE user_id = @UserId",
                new { UserId = userId },
                tx,
                cancellationToken: ct))) ?? 0;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_recent_tickets (user_id, ticket_id, position, added_utc)
            VALUES (@UserId, @TicketId, @Position, now())
            """,
            new { UserId = userId, TicketId = ticketId, Position = nextPosition },
            tx,
            cancellationToken: ct));

        // Trim the head — oldest by position — when over the cap. We
        // prune by position rather than by added_utc so a manually-
        // reordered list drops the topmost entry that the user has
        // shoved to the start, matching the previous in-memory behaviour.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM user_recent_tickets
            WHERE user_id = @UserId
              AND ticket_id IN (
                  SELECT ticket_id
                    FROM user_recent_tickets
                    WHERE user_id = @UserId
                    ORDER BY position ASC, added_utc ASC
                    OFFSET @Cap
              )
            """,
            new { UserId = userId, Cap = MaxEntriesPerUser },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid ticketId, CancellationToken ct = default)
    {
        const string sql = """
            DELETE FROM user_recent_tickets
            WHERE user_id = @UserId AND ticket_id = @TicketId
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { UserId = userId, TicketId = ticketId },
            cancellationToken: ct));
        return rows > 0;
    }

    public async Task ReorderAsync(Guid userId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;
        // Deduplicate keeping first occurrence so a malformed client
        // payload can't violate the (user_id, ticket_id) primary key.
        var seen = new HashSet<Guid>();
        var ordered = new List<(Guid TicketId, int Position)>();
        foreach (var id in orderedIds)
        {
            if (!seen.Add(id)) continue;
            ordered.Add((id, ordered.Count));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Only touch rows that actually exist for the user — the client
        // payload is the source of truth for *order*, not for *membership*.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE user_recent_tickets r
                SET position = v.position
                FROM (VALUES @Values) AS v(ticket_id, position)
                WHERE r.user_id = @UserId AND r.ticket_id = v.ticket_id::uuid
            """.Replace("@Values", BuildValuesClause(ordered.Count)),
            BuildReorderParameters(userId, ordered),
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    // Dapper does not expand a VALUES list from a single parameter, so
    // we splice the literal expansion ourselves. The Guid -> text cast
    // happens client-side; the SQL cast back to uuid keeps Postgres happy.
    private static string BuildValuesClause(int count)
    {
        if (count == 0) return "(NULL::text, NULL::int) WHERE FALSE";
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = $"(@T{i}, @P{i})";
        }
        return string.Join(", ", parts);
    }

    private static DynamicParameters BuildReorderParameters(
        Guid userId, IReadOnlyList<(Guid TicketId, int Position)> ordered)
    {
        var p = new DynamicParameters();
        p.Add("UserId", userId);
        for (var i = 0; i < ordered.Count; i++)
        {
            p.Add($"T{i}", ordered[i].TicketId.ToString());
            p.Add($"P{i}", ordered[i].Position);
        }
        return p;
    }
}
