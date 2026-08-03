using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Timesheet;

// ---- DTOs ----------------------------------------------------------------

/// One row in a user's Comments inbox: a thread they are linked into that is
/// not hidden (its origin row is not yet BO-checked). Sealed class + AS-
/// PascalCase aliases per the Dapper conventions.
public sealed class CommentInboxRow
{
    public Guid ThreadId { get; set; }
    public long TicketNumber { get; set; }
    public Guid? TicketId { get; set; }
    public string OriginContext { get; set; } = string.Empty;
    // v0.0.89 — the exact origin row (sales-receipt id for 'adsolut', ticket id
    // for 'resolved'/'cwi') so the Comments tab can BO-check it directly,
    // writing the same timesheet_bo_checks(entity_id, context) row the origin
    // tab uses (shared state → ticking from either place reflects in both).
    public Guid OriginEntityId { get; set; }
    public DateTime LastMessageUtc { get; set; }
    public string? LastMessagePreview { get; set; }
    public string? LastAuthorEmail { get; set; }
    public int UnreadCount { get; set; }
}

public sealed class CommentMessageRow
{
    public long Id { get; set; }
    public Guid? AuthorId { get; set; }
    public string? AuthorEmail { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class CommentRecipientRow
{
    public long CommentId { get; set; }
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
}

public sealed class CommentUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
}

public sealed record CommentThreadDetail(
    Guid Id,
    long TicketNumber,
    Guid? TicketId,
    string OriginContext,
    Guid? CreatedBy,
    string? CreatedByEmail,
    IReadOnlyList<CommentMessageRow> Messages,
    IReadOnlyList<CommentRecipientRow> Recipients);

public sealed record PostCommentInput(
    Guid? ThreadId,
    long TicketNumber,
    Guid? TicketId,
    string? Context,
    Guid? EntityId,
    string Body,
    IReadOnlyList<Guid> RecipientIds);

/// Outcome of a post. <see cref="Ok"/> false carries an <see cref="Error"/>
/// code; on success the thread/message ids and the recipient user-ids to push
/// a SignalR signal to (author already excluded) are filled.
public sealed record PostCommentResult(
    bool Ok,
    string? Error,
    Guid ThreadId,
    long MessageId,
    long TicketNumber,
    string OriginContext,
    IReadOnlyList<Guid> NotifyUserIds);

public interface ITimesheetCommentService
{
    Task<IReadOnlyList<CommentInboxRow>> GetInboxAsync(Guid viewerId, CancellationToken ct = default);
    Task<int> GetUnreadThreadCountAsync(Guid viewerId, CancellationToken ct = default);
    Task<CommentThreadDetail?> GetThreadAsync(Guid threadId, CancellationToken ct = default);
    Task MarkThreadReadAsync(Guid threadId, Guid viewerId, CancellationToken ct = default);
    Task<PostCommentResult> PostMessageAsync(PostCommentInput input, Guid authorId, CancellationToken ct = default);

    /// Timesheet-active users (the recipient pool). Since v0.0.94 the caller
    /// is included — tagging yourself pins the thread in your own inbox as a
    /// reminder (own message auto-read, so it never lights the red dots).
    Task<IReadOnlyList<CommentUser>> GetRecipientOptionsAsync(CancellationToken ct = default);
}

public sealed class TimesheetCommentService : ITimesheetCommentService
{
    private readonly NpgsqlDataSource _dataSource;

    public TimesheetCommentService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    private static readonly string[] ValidContexts = { "resolved", "cwi", "adsolut" };

    public async Task<IReadOnlyList<CommentInboxRow>> GetInboxAsync(Guid viewerId, CancellationToken ct = default)
    {
        // Threads where the viewer is a recipient of at least one message and
        // whose origin row is not BO-checked (bo.entity_id IS NULL). Preview =
        // the latest message's leading 200 chars; unread = the viewer's still-
        // unread messages in the thread. Newest activity first.
        const string sql = """
            SELECT
                th.id               AS ThreadId,
                th.ticket_number    AS TicketNumber,
                th.ticket_id        AS TicketId,
                th.origin_context   AS OriginContext,
                th.origin_entity_id AS OriginEntityId,
                th.last_message_utc AS LastMessageUtc,
                LEFT(lm.body, 200)  AS LastMessagePreview,
                lmu.email           AS LastAuthorEmail,
                COALESCE(ur.unread_count, 0) AS UnreadCount
            FROM timesheet_comment_threads th
            JOIN LATERAL (
                SELECT 1
                FROM timesheet_comments c
                JOIN timesheet_comment_recipients r ON r.comment_id = c.id
                WHERE c.thread_id = th.id AND r.user_id = @ViewerId
                LIMIT 1
            ) mine ON TRUE
            LEFT JOIN timesheet_bo_checks bo
                ON bo.entity_id = th.origin_entity_id AND bo.context = th.origin_context
            LEFT JOIN LATERAL (
                SELECT c.body, c.author_id
                FROM timesheet_comments c
                WHERE c.thread_id = th.id
                ORDER BY c.created_utc DESC, c.id DESC
                LIMIT 1
            ) lm ON TRUE
            LEFT JOIN users lmu ON lmu.id = lm.author_id
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS unread_count
                FROM timesheet_comments c
                JOIN timesheet_comment_recipients r ON r.comment_id = c.id
                WHERE c.thread_id = th.id AND r.user_id = @ViewerId AND r.read_utc IS NULL
            ) ur ON TRUE
            WHERE bo.entity_id IS NULL
            ORDER BY th.last_message_utc DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CommentInboxRow>(new CommandDefinition(
            sql, new { ViewerId = viewerId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> GetUnreadThreadCountAsync(Guid viewerId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*)::int
            FROM timesheet_comment_threads th
            LEFT JOIN timesheet_bo_checks bo
                ON bo.entity_id = th.origin_entity_id AND bo.context = th.origin_context
            WHERE bo.entity_id IS NULL
              AND EXISTS (
                  SELECT 1
                  FROM timesheet_comments c
                  JOIN timesheet_comment_recipients r ON r.comment_id = c.id
                  WHERE c.thread_id = th.id AND r.user_id = @ViewerId AND r.read_utc IS NULL
              )
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, new { ViewerId = viewerId }, cancellationToken: ct));
    }

    public async Task<CommentThreadDetail?> GetThreadAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var meta = await conn.QueryFirstOrDefaultAsync<ThreadMetaRow>(new CommandDefinition(
            """
            SELECT th.id AS Id, th.ticket_number AS TicketNumber, th.ticket_id AS TicketId,
                   th.origin_context AS OriginContext, th.created_by AS CreatedBy, u.email AS CreatedByEmail
            FROM timesheet_comment_threads th
            LEFT JOIN users u ON u.id = th.created_by
            WHERE th.id = @ThreadId
            """,
            new { ThreadId = threadId }, cancellationToken: ct));
        if (meta is null) return null;

        var messages = (await conn.QueryAsync<CommentMessageRow>(new CommandDefinition(
            """
            SELECT c.id AS Id, c.author_id AS AuthorId, u.email AS AuthorEmail,
                   c.body AS Body, c.created_utc AS CreatedUtc
            FROM timesheet_comments c
            LEFT JOIN users u ON u.id = c.author_id
            WHERE c.thread_id = @ThreadId
            ORDER BY c.created_utc ASC, c.id ASC
            """,
            new { ThreadId = threadId }, cancellationToken: ct))).ToList();

        var recipients = (await conn.QueryAsync<CommentRecipientRow>(new CommandDefinition(
            """
            SELECT r.comment_id AS CommentId, r.user_id AS UserId, u.email AS Email
            FROM timesheet_comment_recipients r
            JOIN timesheet_comments c ON c.id = r.comment_id
            JOIN users u ON u.id = r.user_id
            WHERE c.thread_id = @ThreadId
            ORDER BY u.email
            """,
            new { ThreadId = threadId }, cancellationToken: ct))).ToList();

        return new CommentThreadDetail(
            meta.Id, meta.TicketNumber, meta.TicketId, meta.OriginContext,
            meta.CreatedBy, meta.CreatedByEmail, messages, recipients);
    }

    private sealed class ThreadMetaRow
    {
        public Guid Id { get; set; }
        public long TicketNumber { get; set; }
        public Guid? TicketId { get; set; }
        public string OriginContext { get; set; } = string.Empty;
        public Guid? CreatedBy { get; set; }
        public string? CreatedByEmail { get; set; }
    }

    public async Task MarkThreadReadAsync(Guid threadId, Guid viewerId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE timesheet_comment_recipients r
            SET read_utc = now()
            FROM timesheet_comments c
            WHERE r.comment_id = c.id
              AND c.thread_id = @ThreadId
              AND r.user_id = @ViewerId
              AND r.read_utc IS NULL
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { ThreadId = threadId, ViewerId = viewerId }, cancellationToken: ct));
    }

    public async Task<PostCommentResult> PostMessageAsync(PostCommentInput input, Guid authorId, CancellationToken ct = default)
    {
        var body = (input.Body ?? string.Empty).Trim();
        if (body.Length == 0)
            return Fail("empty_body");
        if (body.Length > 8000)
            body = body[..8000];

        // Tagging yourself is allowed (v0.0.94, also as the only recipient):
        // it pins the thread in your own Comments inbox as a reminder until
        // the origin row is BO-checked. The author's own row is inserted
        // pre-read below so their own words never light the red dots.
        var recipientIds = (input.RecipientIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (recipientIds.Length == 0)
            return Fail("no_recipients");

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Resolve the thread: an explicit id (a reply) is used as-is; otherwise
        // find-or-create by ticket_number. The create branch needs a valid
        // origin (context + entity id) — only the review tabs supply those.
        Guid threadId;
        string originContext;
        long ticketNumber;
        if (input.ThreadId is Guid tid)
        {
            var meta = await conn.QueryFirstOrDefaultAsync<ThreadResolveRow>(new CommandDefinition(
                "SELECT id AS Id, origin_context AS OriginContext, ticket_number AS TicketNumber FROM timesheet_comment_threads WHERE id = @Id",
                new { Id = tid }, transaction: tx, cancellationToken: ct));
            if (meta is null)
                return Fail("thread_not_found");
            threadId = meta.Id;
            originContext = meta.OriginContext;
            ticketNumber = meta.TicketNumber;
        }
        else
        {
            var ctx = (input.Context ?? string.Empty).Trim().ToLowerInvariant();
            if (!ValidContexts.Contains(ctx) || input.EntityId is not Guid entityId || entityId == Guid.Empty)
                return Fail("invalid_origin");

            var newId = Guid.NewGuid();
            var resolved = await conn.QuerySingleAsync<ThreadResolveRow>(new CommandDefinition(
                """
                INSERT INTO timesheet_comment_threads
                    (id, ticket_number, ticket_id, origin_context, origin_entity_id, created_by, created_utc, last_message_utc)
                VALUES (@Id, @TicketNumber, @TicketId, @Context, @EntityId, @AuthorId, now(), now())
                ON CONFLICT (ticket_number) DO UPDATE SET last_message_utc = now()
                RETURNING id AS Id, origin_context AS OriginContext, ticket_number AS TicketNumber
                """,
                new
                {
                    Id = newId,
                    TicketNumber = input.TicketNumber,
                    TicketId = input.TicketId,
                    Context = ctx,
                    EntityId = entityId,
                    AuthorId = authorId,
                },
                transaction: tx, cancellationToken: ct));
            threadId = resolved.Id;
            originContext = resolved.OriginContext;
            ticketNumber = resolved.TicketNumber;
        }

        var messageId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO timesheet_comments (thread_id, author_id, body, created_utc)
            VALUES (@ThreadId, @AuthorId, @Body, now())
            RETURNING id
            """,
            new { ThreadId = threadId, AuthorId = authorId, Body = body },
            transaction: tx, cancellationToken: ct));

        // Insert recipient rows, keeping only ids that are real timesheet-active
        // users — a stale/forged id is silently dropped. A self-tag row starts
        // read (the author has obviously seen their own message), so it keeps
        // the thread in their inbox without a false unread signal. The set we
        // actually linked drives the SignalR fan-out.
        var linked = (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO timesheet_comment_recipients (comment_id, user_id, read_utc)
            SELECT @MessageId, x.id, CASE WHEN x.id = @AuthorId THEN now() END
            FROM unnest(@RecipientIds) AS x(id)
            WHERE EXISTS (
                SELECT 1 FROM users u
                WHERE u.id = x.id
                  AND (u.timesheet_enabled = TRUE OR u.timesheet_manager = TRUE)
                  AND u.role_name IN ('Agent','Admin')
                  AND u.is_active = TRUE
            )
            RETURNING user_id
            """,
            new { MessageId = messageId, AuthorId = authorId, RecipientIds = recipientIds },
            transaction: tx, cancellationToken: ct))).ToList();

        if (linked.Count == 0)
        {
            // No id resolved to a valid recipient — abort rather than leave a
            // message nobody is linked to (linking is mandatory).
            await tx.RollbackAsync(ct);
            return Fail("no_valid_recipients");
        }

        // Never push a "you commented" signal (with toast) at the author —
        // their own client refreshes via the post mutation's invalidation.
        var notify = linked.Where(id => id != authorId).ToList();

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE timesheet_comment_threads SET last_message_utc = now() WHERE id = @ThreadId",
            new { ThreadId = threadId }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new PostCommentResult(true, null, threadId, messageId, ticketNumber, originContext, notify);
    }

    private sealed class ThreadResolveRow
    {
        public Guid Id { get; set; }
        public string OriginContext { get; set; } = string.Empty;
        public long TicketNumber { get; set; }
    }

    private static PostCommentResult Fail(string error)
        => new(false, error, Guid.Empty, 0, 0, string.Empty, Array.Empty<Guid>());

    public async Task<IReadOnlyList<CommentUser>> GetRecipientOptionsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS Id, email AS Email
            FROM users
            WHERE (timesheet_enabled = TRUE OR timesheet_manager = TRUE)
              AND role_name IN ('Agent','Admin')
              AND is_active = TRUE
            ORDER BY email
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CommentUser>(new CommandDefinition(
            sql, cancellationToken: ct));
        return rows.ToList();
    }
}
