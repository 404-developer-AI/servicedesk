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
               processing_state  AS ProcessingState
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
}
