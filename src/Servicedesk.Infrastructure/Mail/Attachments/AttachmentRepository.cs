using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Mail.Attachments;

public sealed class AttachmentRepository : IAttachmentRepository
{
    private const string SelectColumns = """
        SELECT id                AS Id,
               owner_id          AS OwnerId,
               owner_kind        AS OwnerKind,
               content_hash      AS ContentHash,
               size_bytes        AS SizeBytes,
               mime_type         AS MimeType,
               original_filename AS OriginalFilename,
               is_inline         AS IsInline,
               content_id        AS ContentId,
               processing_state  AS ProcessingState,
               event_id          AS EventId
        """;

    private readonly NpgsqlDataSource _dataSource;

    public AttachmentRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AttachmentRow?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = SelectColumns + " FROM attachments WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AttachmentRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AttachmentRow>> ListByMailAsync(Guid mailId, CancellationToken ct)
    {
        var sql = SelectColumns + " FROM attachments WHERE owner_kind = 'Mail' AND owner_id = @mailId ORDER BY created_utc, id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AttachmentRow>(
            new CommandDefinition(sql, new { mailId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AttachmentRow>> ListByEventAsync(long eventId, CancellationToken ct)
    {
        var sql = SelectColumns + " FROM attachments WHERE event_id = @eventId ORDER BY created_utc, id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AttachmentRow>(
            new CommandDefinition(sql, new { eventId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> MarkReadyAsync(Guid attachmentId, string contentHash, long sizeBytes, string mimeType, CancellationToken ct)
    {
        const string sql = """
            UPDATE attachments
               SET content_hash    = @contentHash,
                   size_bytes      = @sizeBytes,
                   mime_type       = @mimeType,
                   processing_state = 'Ready'
             WHERE id = @attachmentId
               AND processing_state = 'Pending'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { attachmentId, contentHash, sizeBytes, mimeType }, cancellationToken: ct));
        return affected == 1;
    }

    public async Task MarkFailedAsync(Guid attachmentId, CancellationToken ct)
    {
        const string sql = """
            UPDATE attachments
               SET processing_state = 'Failed'
             WHERE id = @attachmentId
               AND processing_state = 'Pending'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { attachmentId }, cancellationToken: ct));
    }

    public async Task<Guid> CreateUploadedAsync(NewUploadedAttachment input, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO attachments
                (content_hash, size_bytes, mime_type, original_filename,
                 owner_kind, owner_id, is_inline, content_id, processing_state)
            VALUES
                (@ContentHash, @SizeBytes, @MimeType, @OriginalFilename,
                 'Ticket', @TicketId, FALSE, NULL, 'Ready')
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(
            new CommandDefinition(sql, input, cancellationToken: ct));
    }

    public async Task<int> ReassignToEventAsync(IReadOnlyList<Guid> attachmentIds, Guid ticketId, long eventId, CancellationToken ct)
    {
        if (attachmentIds.Count == 0) return 0;
        // Ownership guard: only flip rows that are still staged on *this*
        // ticket and have no event_id yet. An attacker who guessed an
        // attachment id from another ticket is rejected silently — the row
        // count won't match the request and the caller can decide how to
        // surface that.
        const string sql = """
            UPDATE attachments
               SET event_id = @eventId
             WHERE id = ANY(@ids)
               AND owner_kind = 'Ticket'
               AND owner_id   = @ticketId
               AND event_id IS NULL
               AND processing_state = 'Ready'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { ids = attachmentIds.ToArray(), ticketId, eventId }, cancellationToken: ct));
    }

    public async Task<int> ReassignToMailAsync(IReadOnlyList<AttachmentReassignToMail> assignments, Guid ticketId, Guid mailMessageId, long ticketEventId, CancellationToken ct)
    {
        if (assignments.Count == 0) return 0;
        // Per-row update because content_id + is_inline differ. Single
        // transaction keeps the move atomic so a failure mid-batch can't
        // leave half the attachments on the ticket and half on the mail.
        // event_id is set alongside owner_kind='Mail' so the timeline-enricher
        // can find these rows via either ListByMailAsync or ListByEventAsync;
        // outbound mail thus lights up both code paths uniformly with inbound.
        const string sql = """
            UPDATE attachments
               SET owner_kind = 'Mail',
                   owner_id   = @MailMessageId,
                   event_id   = @TicketEventId,
                   is_inline  = @IsInline,
                   content_id = @ContentId
             WHERE id = @AttachmentId
               AND owner_kind = 'Ticket'
               AND owner_id   = @TicketId
               AND processing_state = 'Ready'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var moved = 0;
        foreach (var a in assignments)
        {
            moved += await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                a.AttachmentId,
                MailMessageId = mailMessageId,
                TicketEventId = ticketEventId,
                TicketId = ticketId,
                a.IsInline,
                a.ContentId,
            }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return moved;
    }
}
