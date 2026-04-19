using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Notifications;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public NotificationRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<UserNotificationRow>> CreateManyAsync(
        IReadOnlyList<NewUserNotification> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<UserNotificationRow>();

        // Single round-trip multi-row insert via UNNEST — keeps us on one
        // parameter set even when 20+ agents are tagged in a single post.
        // RETURNING is LEFT JOIN'd back to `users` for the source email so
        // the navbar / toast can render `@{localPart}` without an extra
        // fetch per row.
        const string sql = """
            WITH inserted AS (
                INSERT INTO user_notifications (
                    user_id, source_user_id, notification_type, ticket_id,
                    ticket_number, ticket_subject, event_id, event_type, preview_text
                )
                SELECT
                    user_id, source_user_id, notification_type, ticket_id,
                    ticket_number, ticket_subject, event_id, event_type, preview_text
                FROM UNNEST(
                    @UserIds::uuid[], @SourceUserIds::uuid[], @Types::text[], @TicketIds::uuid[],
                    @TicketNumbers::bigint[], @TicketSubjects::text[], @EventIds::bigint[],
                    @EventTypes::text[], @PreviewTexts::text[]
                ) AS src (
                    user_id, source_user_id, notification_type, ticket_id,
                    ticket_number, ticket_subject, event_id, event_type, preview_text
                )
                RETURNING id, user_id, source_user_id, notification_type, ticket_id,
                          ticket_number, ticket_subject, event_id, event_type, preview_text,
                          created_utc, viewed_utc, acked_utc, email_sent_utc, email_error
            )
            SELECT
                i.id AS Id,
                i.user_id AS UserId,
                i.source_user_id AS SourceUserId,
                su.email AS SourceUserEmail,
                i.notification_type AS NotificationType,
                i.ticket_id AS TicketId,
                i.ticket_number AS TicketNumber,
                i.ticket_subject AS TicketSubject,
                i.event_id AS EventId,
                i.event_type AS EventType,
                i.preview_text AS PreviewText,
                i.created_utc AS CreatedUtc,
                i.viewed_utc AS ViewedUtc,
                i.acked_utc AS AckedUtc,
                i.email_sent_utc AS EmailSentUtc,
                i.email_error AS EmailError
            FROM inserted i
            LEFT JOIN users su ON su.id = i.source_user_id
            ORDER BY i.created_utc, i.id
            """;

        var userIds = new Guid[rows.Count];
        var sourceUserIds = new Guid?[rows.Count];
        var types = new string[rows.Count];
        var ticketIds = new Guid[rows.Count];
        var ticketNumbers = new long[rows.Count];
        var ticketSubjects = new string[rows.Count];
        var eventIds = new long[rows.Count];
        var eventTypes = new string[rows.Count];
        var previews = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            userIds[i] = r.UserId;
            sourceUserIds[i] = r.SourceUserId;
            types[i] = r.NotificationType;
            ticketIds[i] = r.TicketId;
            ticketNumbers[i] = r.TicketNumber;
            ticketSubjects[i] = r.TicketSubject;
            eventIds[i] = r.EventId;
            eventTypes[i] = r.EventType;
            previews[i] = r.PreviewText;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var inserted = await connection.QueryAsync<UserNotificationRow>(
            new CommandDefinition(sql, new
            {
                UserIds = userIds,
                SourceUserIds = sourceUserIds,
                Types = types,
                TicketIds = ticketIds,
                TicketNumbers = ticketNumbers,
                TicketSubjects = ticketSubjects,
                EventIds = eventIds,
                EventTypes = eventTypes,
                PreviewTexts = previews,
            }, cancellationToken: ct));
        return inserted.ToList();
    }

    public async Task<IReadOnlyList<UserNotificationRow>> ListPendingForUserAsync(
        Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT n.id AS Id, n.user_id AS UserId, n.source_user_id AS SourceUserId,
                   su.email AS SourceUserEmail,
                   n.notification_type AS NotificationType,
                   n.ticket_id AS TicketId, n.ticket_number AS TicketNumber,
                   n.ticket_subject AS TicketSubject,
                   n.event_id AS EventId, n.event_type AS EventType,
                   n.preview_text AS PreviewText,
                   n.created_utc AS CreatedUtc, n.viewed_utc AS ViewedUtc,
                   n.acked_utc AS AckedUtc, n.email_sent_utc AS EmailSentUtc,
                   n.email_error AS EmailError
            FROM user_notifications n
            LEFT JOIN users su ON su.id = n.source_user_id
            WHERE n.user_id = @userId AND n.acked_utc IS NULL
            ORDER BY n.created_utc DESC, n.id DESC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<UserNotificationRow>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<UserNotificationRow>> ListHistoryForUserAsync(
        Guid userId, NotificationHistoryCursor? cursor, int limit, CancellationToken ct)
    {
        // Keyset pagination: `WHERE (created_utc, id) < (@cursorUtc, @cursorId)`.
        // The comparison is a row-wise compare so the index
        // ix_user_notifications_user_history does a pure range-scan and never
        // needs to materialise an offset.
        var effectiveLimit = limit <= 0 ? 50 : Math.Min(limit, 200);
        const string sqlNoCursor = """
            SELECT n.id AS Id, n.user_id AS UserId, n.source_user_id AS SourceUserId,
                   su.email AS SourceUserEmail,
                   n.notification_type AS NotificationType,
                   n.ticket_id AS TicketId, n.ticket_number AS TicketNumber,
                   n.ticket_subject AS TicketSubject,
                   n.event_id AS EventId, n.event_type AS EventType,
                   n.preview_text AS PreviewText,
                   n.created_utc AS CreatedUtc, n.viewed_utc AS ViewedUtc,
                   n.acked_utc AS AckedUtc, n.email_sent_utc AS EmailSentUtc,
                   n.email_error AS EmailError
            FROM user_notifications n
            LEFT JOIN users su ON su.id = n.source_user_id
            WHERE n.user_id = @userId
            ORDER BY n.created_utc DESC, n.id DESC
            LIMIT @limit
            """;
        const string sqlCursor = """
            SELECT n.id AS Id, n.user_id AS UserId, n.source_user_id AS SourceUserId,
                   su.email AS SourceUserEmail,
                   n.notification_type AS NotificationType,
                   n.ticket_id AS TicketId, n.ticket_number AS TicketNumber,
                   n.ticket_subject AS TicketSubject,
                   n.event_id AS EventId, n.event_type AS EventType,
                   n.preview_text AS PreviewText,
                   n.created_utc AS CreatedUtc, n.viewed_utc AS ViewedUtc,
                   n.acked_utc AS AckedUtc, n.email_sent_utc AS EmailSentUtc,
                   n.email_error AS EmailError
            FROM user_notifications n
            LEFT JOIN users su ON su.id = n.source_user_id
            WHERE n.user_id = @userId
              AND (n.created_utc, n.id) < (@cursorUtc, @cursorId)
            ORDER BY n.created_utc DESC, n.id DESC
            LIMIT @limit
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var sql = cursor is null ? sqlNoCursor : sqlCursor;
        var parameters = cursor is null
            ? (object)new { userId, limit = effectiveLimit }
            : new { userId, limit = effectiveLimit, cursorUtc = cursor.CreatedUtc, cursorId = cursor.Id };
        var rows = await connection.QueryAsync<UserNotificationRow>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<UserNotificationRow?> GetByIdForUserAsync(
        Guid id, Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT n.id AS Id, n.user_id AS UserId, n.source_user_id AS SourceUserId,
                   su.email AS SourceUserEmail,
                   n.notification_type AS NotificationType,
                   n.ticket_id AS TicketId, n.ticket_number AS TicketNumber,
                   n.ticket_subject AS TicketSubject,
                   n.event_id AS EventId, n.event_type AS EventType,
                   n.preview_text AS PreviewText,
                   n.created_utc AS CreatedUtc, n.viewed_utc AS ViewedUtc,
                   n.acked_utc AS AckedUtc, n.email_sent_utc AS EmailSentUtc,
                   n.email_error AS EmailError
            FROM user_notifications n
            LEFT JOIN users su ON su.id = n.source_user_id
            WHERE n.id = @id AND n.user_id = @userId
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<UserNotificationRow>(
            new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }

    public async Task<bool> MarkViewedAsync(Guid id, Guid userId, CancellationToken ct)
    {
        // Idempotent — if the row is already acked the WHERE short-circuits
        // and the endpoint returns 204 regardless (the client never needs to
        // know whether this is the transition or a re-click).
        const string sql = """
            UPDATE user_notifications
            SET viewed_utc = COALESCE(viewed_utc, now()),
                acked_utc  = COALESCE(acked_utc,  now())
            WHERE id = @id AND user_id = @userId AND acked_utc IS NULL
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> MarkAckedAsync(Guid id, Guid userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE user_notifications
            SET acked_utc = COALESCE(acked_utc, now())
            WHERE id = @id AND user_id = @userId AND acked_utc IS NULL
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<int> MarkAllAckedAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE user_notifications
            SET acked_utc = now()
            WHERE user_id = @userId AND acked_utc IS NULL
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task MarkEmailSentAsync(Guid id, DateTime? sentUtc, string? error, CancellationToken ct)
    {
        const string sql = """
            UPDATE user_notifications
            SET email_sent_utc = @sentUtc, email_error = @error
            WHERE id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id, sentUtc, error }, cancellationToken: ct));
    }
}
