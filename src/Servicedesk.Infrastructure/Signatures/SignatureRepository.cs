using Dapper;
using Npgsql;
using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

public sealed class SignatureRepository : ISignatureRepository
{
    private const string SelectColumns = """
        id              AS Id,
        name            AS Name,
        design::text    AS DesignJson,
        is_system       AS IsSystem,
        enabled         AS Enabled,
        sort_order      AS SortOrder,
        created_utc     AS CreatedUtc,
        updated_utc     AS UpdatedUtc,
        created_by      AS CreatedBy
        """;

    private readonly NpgsqlDataSource _dataSource;

    public SignatureRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<Signature>> ListAsync(CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM mail_signatures ORDER BY is_system DESC, sort_order, lower(name)";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<Signature?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM mail_signatures WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    public async Task<Guid> CreateAsync(
        string name, SignatureDesign design, bool isSystem, bool enabled,
        int sortOrder, Guid? createdBy, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mail_signatures (name, design, is_system, enabled, sort_order, created_by)
            VALUES (@name, @design::jsonb, @isSystem, @enabled, @sortOrder, @createdBy)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                name,
                design = SignatureJson.Serialize(design),
                isSystem,
                enabled,
                sortOrder,
                createdBy,
            },
            cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(
        Guid id, string name, SignatureDesign design, bool isSystem, bool enabled,
        int sortOrder, CancellationToken ct)
    {
        const string sql = """
            UPDATE mail_signatures
            SET name = @name,
                design = @design::jsonb,
                is_system = @isSystem,
                enabled = @enabled,
                sort_order = @sortOrder,
                updated_utc = now()
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id,
                name,
                design = SignatureJson.Serialize(design),
                isSystem,
                enabled,
                sortOrder,
            },
            cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM mail_signatures WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<Signature?> ResolveForQueueAsync(Guid queueId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM mail_signatures s
            JOIN mail_signature_mailboxes m ON m.signature_id = s.id
            WHERE m.queue_id = @queueId AND s.enabled = TRUE
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    // ---- assets ----

    public async Task<IReadOnlyList<SignatureAsset>> ListAssetsAsync(Guid signatureId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT {AssetColumns} FROM signature_assets
            WHERE signature_id = @signatureId
            ORDER BY created_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AssetRow>(new CommandDefinition(sql, new { signatureId }, cancellationToken: ct));
        return rows.Select(MapAsset).ToList();
    }

    public async Task<SignatureAsset?> GetAssetAsync(Guid assetId, CancellationToken ct)
    {
        const string sql = $"SELECT {AssetColumns} FROM signature_assets WHERE id = @assetId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<AssetRow>(new CommandDefinition(sql, new { assetId }, cancellationToken: ct));
        return row is null ? null : MapAsset(row);
    }

    public async Task<Guid> AddAssetAsync(
        Guid signatureId, string contentHash, string mimeType,
        string originalFilename, long sizeBytes, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO signature_assets (signature_id, content_hash, mime_type, original_filename, size_bytes)
            VALUES (@signatureId, @contentHash, @mimeType, @originalFilename, @sizeBytes)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { signatureId, contentHash, mimeType, originalFilename, sizeBytes },
            cancellationToken: ct));
    }

    public async Task<bool> DeleteAssetAsync(Guid assetId, CancellationToken ct)
    {
        const string sql = "DELETE FROM signature_assets WHERE id = @assetId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { assetId }, cancellationToken: ct));
        return rows > 0;
    }

    // ---- mailbox bindings ----

    public async Task<IReadOnlyList<SignatureMailbox>> ListMailboxesAsync(CancellationToken ct)
    {
        const string sql = "SELECT queue_id AS QueueId, signature_id AS SignatureId FROM mail_signature_mailboxes";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SignatureMailbox>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListQueuesForSignatureAsync(Guid signatureId, CancellationToken ct)
    {
        const string sql = "SELECT queue_id FROM mail_signature_mailboxes WHERE signature_id = @signatureId ORDER BY queue_id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { signatureId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SetMailboxAsync(Guid queueId, Guid signatureId, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mail_signature_mailboxes (queue_id, signature_id)
            VALUES (@queueId, @signatureId)
            ON CONFLICT (queue_id) DO UPDATE SET signature_id = EXCLUDED.signature_id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { queueId, signatureId }, cancellationToken: ct));
    }

    public async Task<bool> ClearMailboxAsync(Guid queueId, CancellationToken ct)
    {
        const string sql = "DELETE FROM mail_signature_mailboxes WHERE queue_id = @queueId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task SetQueuesForSignatureAsync(Guid signatureId, IReadOnlyList<Guid> queueIds, CancellationToken ct)
    {
        // Atomic replace of this signature's mailbox set. Because queue_id is the
        // PK, assigning a queue here also detaches it from any other signature
        // (DO UPDATE), which is exactly the "one signature per mailbox" rule.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Drop bindings this signature no longer owns.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mail_signature_mailboxes WHERE signature_id = @signatureId AND queue_id <> ALL(@queueIds)",
            new { signatureId, queueIds = queueIds.ToArray() }, tx, cancellationToken: ct));

        foreach (var queueId in queueIds.Distinct())
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mail_signature_mailboxes (queue_id, signature_id)
                VALUES (@queueId, @signatureId)
                ON CONFLICT (queue_id) DO UPDATE SET signature_id = EXCLUDED.signature_id
                """,
                new { queueId, signatureId }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    private const string AssetColumns = """
        id                  AS Id,
        signature_id        AS SignatureId,
        content_hash        AS ContentHash,
        mime_type           AS MimeType,
        original_filename   AS OriginalFilename,
        size_bytes          AS SizeBytes,
        created_utc         AS CreatedUtc
        """;

    private static Signature MapToDomain(Row r) => new(
        r.Id,
        r.Name,
        SignatureJson.Deserialize(r.DesignJson),
        r.IsSystem,
        r.Enabled,
        r.SortOrder,
        r.CreatedUtc,
        r.UpdatedUtc,
        r.CreatedBy);

    private static SignatureAsset MapAsset(AssetRow r) => new(
        r.Id, r.SignatureId, r.ContentHash, r.MimeType, r.OriginalFilename, r.SizeBytes, r.CreatedUtc);

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DesignJson { get; set; } = "{}";
        public bool IsSystem { get; set; }
        public bool Enabled { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
    }

    private sealed class AssetRow
    {
        public Guid Id { get; set; }
        public Guid SignatureId { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public string OriginalFilename { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
