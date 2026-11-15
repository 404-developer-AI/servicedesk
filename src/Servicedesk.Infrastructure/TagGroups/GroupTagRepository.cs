using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.TagGroups;

public sealed record NewGroupTag(
    Guid GroupId, Guid TicketId, long TicketNumber, string TicketSubject,
    long EventId, string EventType, Guid? SourceUserId, string PreviewText);

/// <c>PersonalAck</c> is the group's mode snapshotted at tag time (every
/// member reads it for themselves). <c>ReadByMeUtc</c> is the caller's own
/// read; <c>ReadCount</c> / <c>RecipientCount</c> ("read by X of Y") are only
/// filled on the history page, for personal tags.
public sealed record GroupTagRow(
    Guid Id, Guid GroupId, string GroupName, Guid TicketId, long TicketNumber, string TicketSubject,
    long EventId, string EventType, Guid? SourceUserId, string? SourceUserEmail, string PreviewText,
    DateTime CreatedUtc, DateTime? AckedUtc, Guid? AckedByUserId, string? AckedByEmail,
    bool PersonalAck, DateTime? ReadByMeUtc, int ReadCount, int RecipientCount);

public sealed record GroupTagCursor(DateTime CreatedUtc, Guid Id);

/// A successful acknowledgement: the tag's group and whether it was a
/// personal read (caller only) or a shared ack (whole group).
public sealed record GroupTagAck(Guid GroupId, bool Personal);

public interface IGroupTagRepository
{
    Task<IReadOnlyList<GroupTagRow>> CreateManyAsync(IReadOnlyList<NewGroupTag> rows, CancellationToken ct);
    /// Open tags of the active groups the user currently belongs to: shared
    /// tags nobody acknowledged yet, personal tags the user has not read.
    Task<IReadOnlyList<GroupTagRow>> ListPendingForUserAsync(Guid userId, CancellationToken ct);
    /// Every tag (open and acknowledged) visible to the user, newest first.
    Task<IReadOnlyList<GroupTagRow>> ListHistoryForUserAsync(Guid userId, GroupTagCursor? cursor, int limit, CancellationToken ct);
    /// Acknowledges the tag in one guarded statement, only for a current
    /// member of the (active) group who can see it. Shared tag → cleared for
    /// the whole group while still open (two members clicking at once can
    /// never both win); personal tag → marked read for the caller only.
    /// Null = nothing changed (gone, already acked/read, or not visible).
    Task<GroupTagAck?> AckAsync(Guid tagId, Guid userId, CancellationToken ct);
    /// Whether the tag exists and is visible to the user.
    Task<bool> IsVisibleToAsync(Guid tagId, Guid userId, CancellationToken ct);
}

/// v0.1.54 — group tags: one row per group per @@-mention.
/// Shared mode: acknowledging it once clears it for everybody.
/// Personal mode (snapshot of <c>tag_groups.personal_ack</c>, e.g. an
/// "Everyone" group): every member who was in the group when the tag was
/// made reads it for themselves (<c>group_tag_reads</c>); <c>acked_utc</c>
/// stays NULL, so retention treats it as an open tag.
public sealed class GroupTagRepository : IGroupTagRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public GroupTagRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string Columns = """
        t.id AS Id, t.group_id AS GroupId, g.name AS GroupName,
        t.ticket_id AS TicketId, t.ticket_number AS TicketNumber, t.ticket_subject AS TicketSubject,
        t.event_id AS EventId, t.event_type AS EventType,
        t.source_user_id AS SourceUserId, su.email::text AS SourceUserEmail,
        t.preview_text AS PreviewText, t.created_utc AS CreatedUtc,
        t.acked_utc AS AckedUtc, t.acked_by_user_id AS AckedByUserId, au.email::text AS AckedByEmail,
        t.personal_ack AS PersonalAck
        """;

    private const string BaseJoins = """

        FROM group_tags t
        JOIN tag_groups g ON g.id = t.group_id
        LEFT JOIN users su ON su.id = t.source_user_id
        LEFT JOIN users au ON au.id = t.acked_by_user_id
        """;

    /// The user's view. Membership is read live (a member added later sees
    /// the group's open shared tags, a removed one stops seeing them), only
    /// active groups, and a personal tag only for members who were already
    /// in the group when it was made — a new colleague in "Everyone" does
    /// not inherit the backlog.
    private const string ForUser = """

        JOIN tag_group_members me ON me.group_id = t.group_id AND me.user_id = @userId
        LEFT JOIN group_tag_reads mr ON mr.tag_id = t.id AND mr.user_id = @userId
        WHERE g.is_active AND (NOT t.personal_ack OR t.created_utc >= me.added_utc)
        """;

    /// "Read by X of Y" for personal tags: recipients are the current
    /// Agent/Admin members who were in the group at tag time.
    private const string Counts = """
        CASE WHEN t.personal_ack THEN (
            SELECT COUNT(*)::int FROM group_tag_reads r
            JOIN tag_group_members rm ON rm.group_id = t.group_id AND rm.user_id = r.user_id
                                     AND rm.added_utc <= t.created_utc
            JOIN users ru ON ru.id = rm.user_id AND ru.role_name IN ('Agent', 'Admin')
            WHERE r.tag_id = t.id) ELSE 0 END AS ReadCount,
        CASE WHEN t.personal_ack THEN (
            SELECT COUNT(*)::int FROM tag_group_members rm
            JOIN users ru ON ru.id = rm.user_id AND ru.role_name IN ('Agent', 'Admin')
            WHERE rm.group_id = t.group_id AND rm.added_utc <= t.created_utc) ELSE 0 END AS RecipientCount
        """;

    public async Task<IReadOnlyList<GroupTagRow>> CreateManyAsync(IReadOnlyList<NewGroupTag> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<GroupTagRow>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = new List<Guid>();
        foreach (var r in rows)
        {
            // The group's acknowledgement mode is copied onto the tag, so a
            // later switch never re-opens or closes existing tags.
            ids.Add(await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO group_tags (group_id, ticket_id, ticket_number, ticket_subject, event_id,
                                        event_type, source_user_id, preview_text, personal_ack)
                VALUES (@GroupId, @TicketId, @TicketNumber, @TicketSubject, @EventId,
                        @EventType, @SourceUserId, @PreviewText,
                        COALESCE((SELECT personal_ack FROM tag_groups WHERE id = @GroupId), FALSE))
                RETURNING id
                """, r, tx, cancellationToken: ct)));
        }
        await tx.CommitAsync(ct);
        var created = await conn.QueryAsync<GroupTagRow>(new CommandDefinition(
            "SELECT " + Columns + ", NULL::timestamptz AS ReadByMeUtc, 0 AS ReadCount, 0 AS RecipientCount"
            + BaseJoins + " WHERE t.id = ANY(@ids) ORDER BY t.created_utc, t.id",
            new { ids = ids.ToArray() }, cancellationToken: ct));
        return created.ToList();
    }

    public async Task<IReadOnlyList<GroupTagRow>> ListPendingForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GroupTagRow>(new CommandDefinition(
            "SELECT " + Columns + ", mr.read_utc AS ReadByMeUtc, 0 AS ReadCount, 0 AS RecipientCount"
            + BaseJoins + ForUser + """

                 AND CASE WHEN t.personal_ack THEN mr.tag_id IS NULL ELSE t.acked_utc IS NULL END
                ORDER BY t.created_utc DESC, t.id DESC
                LIMIT 200
                """,
            new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<GroupTagRow>> ListHistoryForUserAsync(
        Guid userId, GroupTagCursor? cursor, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GroupTagRow>(new CommandDefinition(
            "SELECT " + Columns + ", mr.read_utc AS ReadByMeUtc, " + Counts
            + BaseJoins + ForUser + """

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

    public async Task<GroupTagAck?> AckAsync(Guid tagId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // One statement: resolve the visible tag, then either the guarded
        // shared UPDATE or the idempotent personal read INSERT. The UPDATE
        // re-checks acked_utc IS NULL after a row-lock wait, so concurrent
        // shared acks have exactly one winner.
        return await conn.QuerySingleOrDefaultAsync<GroupTagAck>(new CommandDefinition("""
            WITH tag AS (
                SELECT t.id, t.group_id, t.personal_ack
                FROM group_tags t
                JOIN tag_groups g ON g.id = t.group_id AND g.is_active
                JOIN tag_group_members me ON me.group_id = t.group_id AND me.user_id = @userId
                WHERE t.id = @tagId AND (NOT t.personal_ack OR t.created_utc >= me.added_utc)
            ), shared AS (
                UPDATE group_tags t
                   SET acked_utc = now(), acked_by_user_id = @userId
                  FROM tag
                 WHERE t.id = tag.id AND NOT tag.personal_ack AND t.acked_utc IS NULL
                RETURNING t.group_id
            ), personal AS (
                INSERT INTO group_tag_reads (tag_id, user_id)
                SELECT tag.id, @userId FROM tag WHERE tag.personal_ack
                ON CONFLICT DO NOTHING
                RETURNING tag_id
            )
            SELECT group_id AS GroupId, FALSE AS Personal FROM shared
            UNION ALL
            SELECT tag.group_id AS GroupId, TRUE AS Personal FROM personal JOIN tag ON tag.id = personal.tag_id
            """, new { tagId, userId }, cancellationToken: ct));
    }

    public async Task<bool> IsVisibleToAsync(Guid tagId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1" + BaseJoins + ForUser + " AND t.id = @tagId)",
            new { tagId, userId }, cancellationToken: ct));
    }
}
