using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.TagGroups;

public sealed record NewGroupTag(
    Guid GroupId, Guid TicketId, long TicketNumber, string TicketSubject,
    long EventId, string EventType, Guid? SourceUserId, string PreviewText);

public sealed record GroupTagRow(
    Guid Id, Guid GroupId, string GroupName, Guid TicketId, long TicketNumber, string TicketSubject,
    long EventId, string EventType, Guid? SourceUserId, string? SourceUserEmail, string PreviewText,
    DateTime CreatedUtc, DateTime? AckedUtc, Guid? AckedByUserId, string? AckedByEmail);

public sealed record GroupTagCursor(DateTime CreatedUtc, Guid Id);

public interface IGroupTagRepository
{
    Task<IReadOnlyList<GroupTagRow>> CreateManyAsync(IReadOnlyList<NewGroupTag> rows, CancellationToken ct);
    /// Open tags of the active groups the user currently belongs to.
    Task<IReadOnlyList<GroupTagRow>> ListPendingForUserAsync(Guid userId, CancellationToken ct);
    /// Every tag (open and acknowledged) of the user's active groups, newest first.
    Task<IReadOnlyList<GroupTagRow>> ListHistoryForUserAsync(Guid userId, GroupTagCursor? cursor, int limit, CancellationToken ct);
    /// Acknowledges the tag for the whole group. One guarded UPDATE: only a
    /// current member of the (active) group, only while it is still open —
    /// two members clicking at once can never both win. Null = not acked
    /// (gone, already acked, or not a member); otherwise the group id.
    Task<Guid?> AckAsync(Guid tagId, Guid userId, CancellationToken ct);
    /// Whether the tag exists and the user is a member of its group.
    Task<bool> IsVisibleToAsync(Guid tagId, Guid userId, CancellationToken ct);
}

/// v0.1.54 — group tags: one row per group per @@-mention, shared by all
/// members (acknowledging it once clears it for everybody).
public sealed class GroupTagRepository : IGroupTagRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public GroupTagRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string Select = """
        SELECT t.id AS Id, t.group_id AS GroupId, g.name AS GroupName,
               t.ticket_id AS TicketId, t.ticket_number AS TicketNumber, t.ticket_subject AS TicketSubject,
               t.event_id AS EventId, t.event_type AS EventType,
               t.source_user_id AS SourceUserId, su.email::text AS SourceUserEmail,
               t.preview_text AS PreviewText, t.created_utc AS CreatedUtc,
               t.acked_utc AS AckedUtc, t.acked_by_user_id AS AckedByUserId, au.email::text AS AckedByEmail
        FROM group_tags t
        JOIN tag_groups g ON g.id = t.group_id
        LEFT JOIN users su ON su.id = t.source_user_id
        LEFT JOIN users au ON au.id = t.acked_by_user_id
        """;

    /// The user's active groups — membership is read live, so a member
    /// added later sees the group's open tags and a removed one stops seeing them.
    private const string MyGroups = """
        t.group_id IN (
            SELECT m.group_id FROM tag_group_members m
            JOIN tag_groups mg ON mg.id = m.group_id AND mg.is_active
            WHERE m.user_id = @userId)
        """;

    public async Task<IReadOnlyList<GroupTagRow>> CreateManyAsync(IReadOnlyList<NewGroupTag> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<GroupTagRow>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = new List<Guid>();
        foreach (var r in rows)
        {
            ids.Add(await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO group_tags (group_id, ticket_id, ticket_number, ticket_subject, event_id,
                                        event_type, source_user_id, preview_text)
                VALUES (@GroupId, @TicketId, @TicketNumber, @TicketSubject, @EventId,
                        @EventType, @SourceUserId, @PreviewText)
                RETURNING id
                """, r, tx, cancellationToken: ct)));
        }
        await tx.CommitAsync(ct);
        var created = await conn.QueryAsync<GroupTagRow>(new CommandDefinition(
            Select + " WHERE t.id = ANY(@ids) ORDER BY t.created_utc, t.id",
            new { ids = ids.ToArray() }, cancellationToken: ct));
        return created.ToList();
    }

    public async Task<IReadOnlyList<GroupTagRow>> ListPendingForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GroupTagRow>(new CommandDefinition(
            Select + " WHERE t.acked_utc IS NULL AND " + MyGroups + " ORDER BY t.created_utc DESC, t.id DESC LIMIT 200",
            new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<GroupTagRow>> ListHistoryForUserAsync(
        Guid userId, GroupTagCursor? cursor, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GroupTagRow>(new CommandDefinition(
            Select + " WHERE " + MyGroups + """
                 AND (@cursorUtc::timestamptz IS NULL OR (t.created_utc, t.id) < (@cursorUtc, @cursorId))
                ORDER BY t.created_utc DESC, t.id DESC
                LIMIT @limit
                """,
            new
            {
                userId,
                cursorUtc = cursor?.CreatedUtc,
                cursorId = cursor?.Id ?? Guid.Empty,
                limit = limit <= 0 ? 50 : Math.Min(limit, 200),
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Guid?> AckAsync(Guid tagId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            UPDATE group_tags t
               SET acked_utc = now(), acked_by_user_id = @userId
             WHERE t.id = @tagId AND t.acked_utc IS NULL
               AND EXISTS (
                   SELECT 1 FROM tag_group_members m
                   JOIN tag_groups g ON g.id = m.group_id AND g.is_active
                   WHERE m.group_id = t.group_id AND m.user_id = @userId)
            RETURNING t.group_id
            """, new { tagId, userId }, cancellationToken: ct));
    }

    public async Task<bool> IsVisibleToAsync(Guid tagId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM group_tags t WHERE t.id = @tagId AND
            """ + " " + MyGroups + ")", new { tagId, userId }, cancellationToken: ct));
    }
}
