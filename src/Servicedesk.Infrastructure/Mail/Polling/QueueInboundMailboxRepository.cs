using Dapper;
using Npgsql;
using Servicedesk.Domain.Taxonomy;

namespace Servicedesk.Infrastructure.Mail.Polling;

public sealed class QueueInboundMailboxRepository : IQueueInboundMailboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public QueueInboundMailboxRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // Column order MUST match the QueueInboundMailbox record's constructor —
    // Dapper's positional-record materializer is order-sensitive.
    private const string SelectColumns = """
        id                            AS Id,
        queue_id                      AS QueueId,
        mailbox_address               AS MailboxAddress,
        folder_id                     AS FolderId,
        folder_name                   AS FolderName,
        polling_enabled               AS PollingEnabled,
        delta_link                    AS DeltaLink,
        last_polled_utc               AS LastPolledUtc,
        last_error                    AS LastError,
        consecutive_failures          AS ConsecutiveFailures,
        processed_folder_id           AS ProcessedFolderId,
        last_mailbox_action_error     AS LastMailboxActionError,
        last_mailbox_action_error_utc AS LastMailboxActionErrorUtc,
        created_utc                   AS CreatedUtc,
        updated_utc                   AS UpdatedUtc
        """;

    // ---------- Reads ----------

    public async Task<IReadOnlyList<QueueInboundMailbox>> ListAllAsync(CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM queue_inbound_mailboxes ORDER BY created_utc, id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<QueueInboundMailbox>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<QueueInboundMailbox>> ListByQueueAsync(Guid queueId, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM queue_inbound_mailboxes WHERE queue_id = @queueId ORDER BY created_utc, id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<QueueInboundMailbox>(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<QueueInboundMailbox?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM queue_inbound_mailboxes WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<QueueInboundMailbox>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> ListAllMailboxAddressesAsync(CancellationToken ct)
    {
        const string sql = "SELECT DISTINCT mailbox_address::text FROM queue_inbound_mailboxes";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<Guid?> FindConflictingQueueAsync(string mailbox, string? folderId, Guid? excludeSourceId, CancellationToken ct)
    {
        // Exclusivity is only enforced once a folder is selected (the unique
        // index is partial WHERE folder_id IS NOT NULL).
        if (string.IsNullOrWhiteSpace(folderId)) return null;

        const string sql = """
            SELECT queue_id
            FROM queue_inbound_mailboxes
            WHERE mailbox_address = @mailbox
              AND folder_id = @folderId
              AND (@excludeSourceId::uuid IS NULL OR id <> @excludeSourceId)
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var queueId = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            sql, new { mailbox, folderId, excludeSourceId }, cancellationToken: ct));
        return queueId;
    }

    // ---------- Config writes ----------

    public async Task<QueueInboundMailbox> AddAsync(Guid queueId, string mailbox, string? folderId, string? folderName, bool pollingEnabled, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO queue_inbound_mailboxes (queue_id, mailbox_address, folder_id, folder_name, polling_enabled)
            VALUES (@queueId, @mailbox, @folderId, @folderName, @pollingEnabled)
            RETURNING {SelectColumns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<QueueInboundMailbox>(new CommandDefinition(
            sql, new { queueId, mailbox, folderId, folderName, pollingEnabled }, cancellationToken: ct));
    }

    public async Task<bool> UpdateConfigAsync(Guid id, string mailbox, string? folderId, string? folderName, bool pollingEnabled, CancellationToken ct)
    {
        // Wipe the delta cursor + backoff + cached processed-folder id whenever
        // the mailbox or folder actually changes — a Graph delta token is bound
        // to the folder it was issued for, and the processed folder belongs to
        // the old mailbox.
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET mailbox_address = @mailbox,
                   folder_id       = @folderId,
                   folder_name     = @folderName,
                   polling_enabled = @pollingEnabled,
                   delta_link = CASE
                       WHEN mailbox_address IS DISTINCT FROM @mailbox
                         OR folder_id       IS DISTINCT FROM @folderId
                       THEN NULL ELSE delta_link END,
                   consecutive_failures = CASE
                       WHEN mailbox_address IS DISTINCT FROM @mailbox
                         OR folder_id       IS DISTINCT FROM @folderId
                       THEN 0 ELSE consecutive_failures END,
                   last_error = CASE
                       WHEN mailbox_address IS DISTINCT FROM @mailbox
                         OR folder_id       IS DISTINCT FROM @folderId
                       THEN NULL ELSE last_error END,
                   processed_folder_id = CASE
                       WHEN mailbox_address IS DISTINCT FROM @mailbox
                       THEN NULL ELSE processed_folder_id END,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { id, mailbox, folderId, folderName, pollingEnabled }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM queue_inbound_mailboxes WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> SetPollingAsync(Guid id, bool enabled, CancellationToken ct)
    {
        // Targeted single-column update — pausing/resuming isn't a content edit,
        // so leave the delta-state intact (resuming picks up where it left off).
        const string sql = "UPDATE queue_inbound_mailboxes SET polling_enabled = @enabled WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, enabled }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task RefreshMirrorAsync(Guid queueId, CancellationToken ct)
    {
        // Mirror the first source onto queues.inbound_* (NULL when no source).
        // Does NOT bump queues.updated_utc — this is a derived sync, not an edit.
        const string sql = """
            WITH first_src AS (
                SELECT mailbox_address, folder_id, folder_name, polling_enabled
                FROM queue_inbound_mailboxes
                WHERE queue_id = @queueId
                ORDER BY created_utc, id
                LIMIT 1
            )
            UPDATE queues q SET
                inbound_mailbox_address = (SELECT mailbox_address FROM first_src),
                inbound_folder_id       = (SELECT folder_id       FROM first_src),
                inbound_folder_name     = (SELECT folder_name     FROM first_src),
                inbound_polling_enabled = COALESCE((SELECT polling_enabled FROM first_src), TRUE)
            WHERE q.id = @queueId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
    }

    // ---------- State writes ----------

    public async Task SaveSuccessAsync(Guid id, string? deltaLink, DateTime polledUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET delta_link = @deltaLink,
                   last_polled_utc = @polledUtc,
                   last_error = NULL,
                   consecutive_failures = 0,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, deltaLink, polledUtc }, cancellationToken: ct));
    }

    public async Task SaveFailureAsync(Guid id, string error, DateTime polledUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET last_polled_utc = @polledUtc,
                   last_error = @error,
                   consecutive_failures = consecutive_failures + 1,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, polledUtc, error }, cancellationToken: ct));
    }

    public async Task ResetFailuresAsync(Guid id, CancellationToken ct)
    {
        // Clears both the delta-polling failure counter and the post-ingest
        // mailbox-action error so acknowledging a mail-polling incident drives
        // the aggregator back to Ok in one shot.
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET last_error = NULL,
                   consecutive_failures = 0,
                   last_mailbox_action_error = NULL,
                   last_mailbox_action_error_utc = NULL,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task SaveProcessedFolderIdAsync(Guid id, string folderId, CancellationToken ct)
    {
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET processed_folder_id = @folderId,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, folderId }, cancellationToken: ct));
    }

    public async Task SaveMailboxActionErrorAsync(Guid id, string error, DateTime occurredUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET last_mailbox_action_error = @error,
                   last_mailbox_action_error_utc = @occurredUtc,
                   updated_utc = now()
             WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, error, occurredUtc }, cancellationToken: ct));
    }

    public async Task ClearMailboxActionErrorAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            UPDATE queue_inbound_mailboxes
               SET last_mailbox_action_error = NULL,
                   last_mailbox_action_error_utc = NULL,
                   updated_utc = now()
             WHERE id = @id
               AND last_mailbox_action_error IS NOT NULL
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }
}
