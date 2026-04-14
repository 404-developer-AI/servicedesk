using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Mail.Ingest;

public sealed class MailMessageRepository : IMailMessageRepository
{
    private const string SelectColumns = """
        SELECT id                   AS Id,
               message_id            AS MessageId,
               in_reply_to           AS InReplyTo,
               subject               AS Subject,
               from_address          AS FromAddress,
               from_name             AS FromName,
               mailbox_address       AS MailboxAddress,
               received_utc          AS ReceivedUtc,
               raw_eml_blob_hash     AS RawEmlBlobHash,
               body_html_blob_hash   AS BodyHtmlBlobHash,
               body_text             AS BodyText,
               ticket_id             AS TicketId,
               ticket_event_id       AS TicketEventId,
               graph_message_id      AS GraphMessageId,
               mailbox_moved_utc     AS MailboxMovedUtc
        """;

    private readonly NpgsqlDataSource _dataSource;

    public MailMessageRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<MailMessageRow?> GetByMessageIdAsync(string internetMessageId, CancellationToken ct)
    {
        var sql = SelectColumns + " FROM mail_messages WHERE message_id = @messageId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MailMessageRow>(
            new CommandDefinition(sql, new { messageId = internetMessageId }, cancellationToken: ct));
    }

    public async Task<MailMessageRow?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = SelectColumns + " FROM mail_messages WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MailMessageRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Guid?> FindTicketIdByReferencesAsync(IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0) return null;
        const string sql = """
            SELECT ticket_id FROM mail_messages
            WHERE message_id = ANY(@ids) AND ticket_id IS NOT NULL
            ORDER BY received_utc DESC
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { ids = messageIds.ToArray() }, cancellationToken: ct));
    }

    public async Task<Guid> InsertAsync(
        NewMailMessage row,
        IReadOnlyList<NewMailRecipient> recipients,
        IReadOnlyList<NewMailAttachment> attachments,
        CancellationToken ct)
    {
        const string insertMail = """
            INSERT INTO mail_messages
                (message_id, in_reply_to, references_header, subject, from_address, from_name,
                 mailbox_address, received_utc, raw_eml_blob_hash, body_html_blob_hash, body_text,
                 graph_message_id)
            VALUES
                (@MessageId, @InReplyTo, @References, @Subject, @FromAddress, @FromName,
                 @MailboxAddress, @ReceivedUtc, @RawEmlBlobHash, @BodyHtmlBlobHash, @BodyText,
                 @GraphMessageId)
            RETURNING id
            """;
        const string insertRecipient = """
            INSERT INTO mail_recipients (mail_id, kind, address, display_name)
            VALUES (@MailId, @Kind, @Address, @DisplayName)
            """;
        const string insertAttachment = """
            INSERT INTO attachments
                (content_hash, size_bytes, mime_type, original_filename,
                 owner_kind, owner_id, is_inline, content_id, processing_state)
            VALUES
                (NULL, @Size, @MimeType, @FileName,
                 'Mail', @MailId, @IsInline, @ContentId, 'Pending')
            RETURNING id
            """;
        const string insertJob = """
            INSERT INTO attachment_jobs (kind, state, payload, next_attempt_utc)
            VALUES ('Ingest', 'Pending', @Payload::jsonb, now())
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var mailId = await conn.ExecuteScalarAsync<Guid>(
            new CommandDefinition(insertMail, row, tx, cancellationToken: ct));

        foreach (var r in recipients)
        {
            await conn.ExecuteAsync(new CommandDefinition(insertRecipient,
                new { MailId = mailId, r.Kind, r.Address, r.DisplayName },
                tx, cancellationToken: ct));
        }

        foreach (var a in attachments)
        {
            var attachmentId = await conn.ExecuteScalarAsync<Guid>(
                new CommandDefinition(insertAttachment,
                    new { MailId = mailId, a.Size, a.MimeType, a.FileName, a.IsInline, a.ContentId },
                    tx, cancellationToken: ct));

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                attachment_id = attachmentId,
                mailbox = a.Mailbox,
                graph_message_id = a.GraphMessageId,
                graph_attachment_id = a.GraphAttachmentId,
            });

            await conn.ExecuteAsync(new CommandDefinition(insertJob,
                new { Payload = payload }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return mailId;
    }

    public async Task AttachToTicketAsync(Guid mailId, Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = """
            UPDATE mail_messages
               SET ticket_id = @ticketId,
                   ticket_event_id = @eventId
             WHERE id = @mailId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { mailId, ticketId, eventId }, cancellationToken: ct));
    }

    public async Task MarkMailboxMovedAsync(Guid mailId, DateTime utc, CancellationToken ct)
    {
        const string sql = """
            UPDATE mail_messages
               SET mailbox_moved_utc = @utc
             WHERE id = @mailId
               AND mailbox_moved_utc IS NULL
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { mailId, utc }, cancellationToken: ct));
    }

    // "Ready to finalize" = mail is attached to a ticket, has not yet been
    // moved, carries the graph_message_id we'll need for the Move call, and
    // every attachment owned by it is in Ready state (absent pending/failed
    // rows). Mails without attachments qualify trivially.
    private const string ReadyForFinalizeWhere = """
        m.ticket_id IS NOT NULL
          AND m.mailbox_moved_utc IS NULL
          AND m.graph_message_id IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM attachments a
               WHERE a.owner_kind = 'Mail' AND a.owner_id = m.id
                 AND a.processing_state <> 'Ready')
        """;

    public async Task<IReadOnlyList<FinalizeCandidate>> ListReadyForFinalizeAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 25;
        if (limit > 500) limit = 500;
        var sql = $"""
            SELECT m.id               AS MailId,
                   m.graph_message_id AS GraphMessageId,
                   m.mailbox_address  AS MailboxAddress
              FROM mail_messages m
             WHERE {ReadyForFinalizeWhere}
             ORDER BY m.received_utc
             LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FinalizeCandidate>(
            new CommandDefinition(sql, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<FinalizeCandidate?> GetIfReadyForFinalizeAsync(Guid mailId, CancellationToken ct)
    {
        var sql = $"""
            SELECT m.id               AS MailId,
                   m.graph_message_id AS GraphMessageId,
                   m.mailbox_address  AS MailboxAddress
              FROM mail_messages m
             WHERE m.id = @mailId AND {ReadyForFinalizeWhere}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<FinalizeCandidate>(
            new CommandDefinition(sql, new { mailId }, cancellationToken: ct));
    }
}
