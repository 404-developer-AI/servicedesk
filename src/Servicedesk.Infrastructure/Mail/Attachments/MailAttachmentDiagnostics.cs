using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Mail.Attachments;

/// Read-only diagnostic view over a mail's attachment rows and the jobs that
/// (tried to) ingest their bytes. Surfaces every piece of state an operator
/// needs to answer "why doesn't this show up on the ticket?": row state, job
/// state, attempt count, last error, and whether the blob actually landed.
public interface IMailAttachmentDiagnostics
{
    Task<MailAttachmentDiagnostic?> GetAsync(Guid mailMessageId, CancellationToken ct);

    /// Most-recently-received mails with at least one attachment row, limited
    /// to <paramref name="limit"/>. When <paramref name="onlyWithIssues"/> is
    /// true only mails with ≥1 non-Ready attachment are returned — the common
    /// "something broke, show me what" entry-point.
    Task<IReadOnlyList<MailAttachmentSummary>> ListRecentAsync(
        int limit, bool onlyWithIssues, CancellationToken ct);
}

public sealed class MailAttachmentDiagnostics : IMailAttachmentDiagnostics
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IBlobStore _blobs;

    public MailAttachmentDiagnostics(NpgsqlDataSource dataSource, IBlobStore blobs)
    {
        _dataSource = dataSource;
        _blobs = blobs;
    }

    public async Task<MailAttachmentDiagnostic?> GetAsync(Guid mailMessageId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        const string mailSql = """
            SELECT id             AS Id,
                   ticket_id      AS TicketId,
                   subject        AS Subject,
                   from_address   AS FromAddress,
                   received_utc   AS ReceivedUtc,
                   body_html_blob_hash AS BodyHtmlBlobHash
              FROM mail_messages
             WHERE id = @mailId
            """;
        var mail = await conn.QueryFirstOrDefaultAsync<MailHeader>(
            new CommandDefinition(mailSql, new { mailId = mailMessageId }, cancellationToken: ct));
        if (mail is null) return null;

        const string attachmentsSql = """
            SELECT id                AS Id,
                   original_filename AS Filename,
                   mime_type         AS MimeType,
                   size_bytes        AS SizeBytes,
                   is_inline         AS IsInline,
                   content_id        AS ContentId,
                   content_hash      AS ContentHash,
                   processing_state  AS ProcessingState,
                   created_utc       AS CreatedUtc
              FROM attachments
             WHERE owner_kind = 'Mail' AND owner_id = @mailId
             ORDER BY created_utc, id
            """;
        var attachmentRows = (await conn.QueryAsync<AttachmentDiagnosticRaw>(
            new CommandDefinition(attachmentsSql, new { mailId = mailMessageId }, cancellationToken: ct))).ToList();

        const string jobsSql = """
            SELECT j.id                                           AS JobId,
                   j.state                                        AS State,
                   j.attempt_count                                AS AttemptCount,
                   j.next_attempt_utc                             AS NextAttemptUtc,
                   j.last_error                                   AS LastError,
                   j.updated_utc                                  AS UpdatedUtc,
                   (j.payload ->> 'attachment_id')::uuid          AS AttachmentId
              FROM attachment_jobs j
             WHERE j.kind = 'Ingest'
               AND (j.payload ->> 'attachment_id')::uuid = ANY(@ids)
            """;
        var ids = attachmentRows.Select(a => a.Id).ToArray();
        var jobRows = ids.Length == 0
            ? new List<AttachmentJobDiagnostic>()
            : (await conn.QueryAsync<AttachmentJobDiagnostic>(
                new CommandDefinition(jobsSql, new { ids }, cancellationToken: ct))).ToList();

        var jobsByAttachment = jobRows
            .GroupBy(j => j.AttachmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.UpdatedUtc).First());

        var items = new List<AttachmentDiagnostic>(attachmentRows.Count);
        foreach (var a in attachmentRows)
        {
            var blobExists = false;
            if (!string.IsNullOrWhiteSpace(a.ContentHash))
            {
                try { blobExists = await _blobs.ExistsAsync(a.ContentHash, ct); }
                catch { blobExists = false; }
            }
            jobsByAttachment.TryGetValue(a.Id, out var job);
            items.Add(new AttachmentDiagnostic(
                a.Id, a.Filename, a.MimeType, a.SizeBytes, a.IsInline, a.ContentId,
                a.ContentHash, a.ProcessingState, a.CreatedUtc, blobExists, job));
        }

        var bodyHtmlBlobPresent = !string.IsNullOrWhiteSpace(mail.BodyHtmlBlobHash)
            && await SafeBlobExists(mail.BodyHtmlBlobHash, ct);

        return new MailAttachmentDiagnostic(
            mail.Id, mail.TicketId, mail.Subject, mail.FromAddress, mail.ReceivedUtc,
            mail.BodyHtmlBlobHash, bodyHtmlBlobPresent, items);
    }

    public async Task<IReadOnlyList<MailAttachmentSummary>> ListRecentAsync(
        int limit, bool onlyWithIssues, CancellationToken ct)
    {
        if (limit <= 0) limit = 25;
        if (limit > 200) limit = 200;

        const string sql = """
            SELECT m.id                                                 AS MailMessageId,
                   m.ticket_id                                          AS TicketId,
                   m.subject                                            AS Subject,
                   m.from_address                                       AS FromAddress,
                   m.received_utc                                       AS ReceivedUtc,
                   COALESCE(a.total, 0)                                 AS AttachmentTotal,
                   COALESCE(a.ready, 0)                                 AS ReadyCount,
                   COALESCE(a.pending, 0)                               AS PendingCount,
                   COALESCE(a.failed, 0)                                AS FailedCount
              FROM mail_messages m
              LEFT JOIN LATERAL (
                  SELECT COUNT(*)                                          AS total,
                         COUNT(*) FILTER (WHERE processing_state='Ready')  AS ready,
                         COUNT(*) FILTER (WHERE processing_state='Pending') AS pending,
                         COUNT(*) FILTER (WHERE processing_state='Failed') AS failed
                    FROM attachments
                   WHERE owner_kind='Mail' AND owner_id = m.id
              ) a ON TRUE
             WHERE COALESCE(a.total, 0) > 0
               AND (@onlyIssues = FALSE OR COALESCE(a.pending,0) + COALESCE(a.failed,0) > 0)
             ORDER BY m.received_utc DESC, m.id DESC
             LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MailAttachmentSummary>(
            new CommandDefinition(sql, new { limit, onlyIssues = onlyWithIssues }, cancellationToken: ct));
        return rows.ToList();
    }

    private async Task<bool> SafeBlobExists(string hash, CancellationToken ct)
    {
        try { return await _blobs.ExistsAsync(hash, ct); }
        catch { return false; }
    }

    private sealed record MailHeader(
        Guid Id, Guid? TicketId, string? Subject, string? FromAddress,
        DateTime ReceivedUtc, string? BodyHtmlBlobHash);

    private sealed record AttachmentDiagnosticRaw(
        Guid Id, string Filename, string MimeType, long SizeBytes, bool IsInline,
        string? ContentId, string? ContentHash, string ProcessingState, DateTime CreatedUtc);
}

public sealed record MailAttachmentDiagnostic(
    Guid MailMessageId,
    Guid? TicketId,
    string? Subject,
    string? FromAddress,
    DateTime ReceivedUtc,
    string? BodyHtmlBlobHash,
    bool BodyHtmlBlobPresent,
    IReadOnlyList<AttachmentDiagnostic> Attachments);

public sealed record AttachmentDiagnostic(
    Guid Id,
    string Filename,
    string MimeType,
    long SizeBytes,
    bool IsInline,
    string? ContentId,
    string? ContentHash,
    string ProcessingState,
    DateTime CreatedUtc,
    bool BlobPresent,
    AttachmentJobDiagnostic? Job);

public sealed record MailAttachmentSummary(
    Guid MailMessageId,
    Guid? TicketId,
    string? Subject,
    string? FromAddress,
    DateTime ReceivedUtc,
    long AttachmentTotal,
    long ReadyCount,
    long PendingCount,
    long FailedCount);

public sealed record AttachmentJobDiagnostic(
    long JobId,
    string State,
    int AttemptCount,
    DateTime NextAttemptUtc,
    string? LastError,
    DateTime UpdatedUtc,
    Guid AttachmentId);
