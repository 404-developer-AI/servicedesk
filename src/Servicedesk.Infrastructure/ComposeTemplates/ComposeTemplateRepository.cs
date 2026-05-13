using Dapper;
using Npgsql;
using Servicedesk.Domain.ComposeTemplates;

namespace Servicedesk.Infrastructure.ComposeTemplates;

public sealed class ComposeTemplateRepository : IComposeTemplateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ComposeTemplateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ComposeTemplate>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = """
            SELECT id          AS Id,
                   name        AS Name,
                   description AS Description,
                   body_html   AS BodyHtml,
                   is_active   AS IsActive,
                   queue_ids   AS QueueIds,
                   created_utc AS CreatedUtc,
                   updated_utc AS UpdatedUtc,
                   created_by  AS CreatedBy
            FROM compose_templates
            """;
        if (!includeInactive) sql += " WHERE is_active = TRUE";
        sql += " ORDER BY is_active DESC, lower(name)";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<ComposeTemplate?> GetAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id          AS Id,
                   name        AS Name,
                   description AS Description,
                   body_html   AS BodyHtml,
                   is_active   AS IsActive,
                   queue_ids   AS QueueIds,
                   created_utc AS CreatedUtc,
                   updated_utc AS UpdatedUtc,
                   created_by  AS CreatedBy
            FROM compose_templates WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    public async Task<IReadOnlyList<ComposeTemplate>> ListForQueueAsync(Guid? queueId, CancellationToken ct)
    {
        // queue_ids = '{}' means "any queue". When the caller passes a
        // concrete queueId we union both buckets. When no queueId is known
        // (e.g. New-Ticket drawer) we only return the unrestricted ones —
        // queue-restricted templates would be misleading at that point.
        var sql = queueId is null
            ? """
              SELECT id          AS Id,
                     name        AS Name,
                     description AS Description,
                     body_html   AS BodyHtml,
                     is_active   AS IsActive,
                     queue_ids   AS QueueIds,
                     created_utc AS CreatedUtc,
                     updated_utc AS UpdatedUtc,
                     created_by  AS CreatedBy
              FROM compose_templates
              WHERE is_active = TRUE AND cardinality(queue_ids) = 0
              ORDER BY lower(name)
              """
            : """
              SELECT id          AS Id,
                     name        AS Name,
                     description AS Description,
                     body_html   AS BodyHtml,
                     is_active   AS IsActive,
                     queue_ids   AS QueueIds,
                     created_utc AS CreatedUtc,
                     updated_utc AS UpdatedUtc,
                     created_by  AS CreatedBy
              FROM compose_templates
              WHERE is_active = TRUE
                AND (cardinality(queue_ids) = 0 OR @queueId = ANY(queue_ids))
              ORDER BY lower(name)
              """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<Guid> CreateAsync(
        string name,
        string? description,
        string bodyHtml,
        IReadOnlyList<Guid> queueIds,
        Guid? createdBy,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO compose_templates (name, description, body_html, queue_ids, created_by)
            VALUES (@name, @description, @bodyHtml, @queueIds, @createdBy)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { name, description, bodyHtml, queueIds = queueIds.ToArray(), createdBy },
            cancellationToken: ct));
    }

    public async Task UpdateAsync(
        Guid id,
        string name,
        string? description,
        string bodyHtml,
        bool isActive,
        IReadOnlyList<Guid> queueIds,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE compose_templates
            SET name = @name,
                description = @description,
                body_html = @bodyHtml,
                is_active = @isActive,
                queue_ids = @queueIds,
                updated_utc = now()
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { id, name, description, bodyHtml, isActive, queueIds = queueIds.ToArray() },
            cancellationToken: ct));
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        const string sql = "UPDATE compose_templates SET is_active = FALSE, updated_utc = now() WHERE id = @id AND is_active = TRUE";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM compose_templates WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    private static ComposeTemplate MapToDomain(Row r) => new(
        r.Id,
        r.Name,
        r.Description,
        r.BodyHtml,
        r.IsActive,
        r.QueueIds ?? Array.Empty<Guid>(),
        r.CreatedUtc,
        r.UpdatedUtc,
        r.CreatedBy);

    // Mutable class for Dapper column-name binding. See the project memo
    // about positional-record-struct null bugs — we avoid those here.
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string BodyHtml { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public Guid[]? QueueIds { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
    }
}
