using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Forms;

public interface IFormTemplateRepository
{
    Task<IReadOnlyList<FormTemplateSummary>> ListAsync(CancellationToken ct);
    Task<FormTemplateDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<Guid> CreateAsync(FormTemplateInput input, Guid? createdByUserId, CancellationToken ct);
    Task<bool> UpdateAsync(Guid id, FormTemplateInput input, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    /// Another active template already uses this name (case-insensitive).
    Task<bool> ActiveNameTakenAsync(string name, Guid? exceptId, CancellationToken ct);
    /// Active templates whose queue scope is empty (all queues) or contains
    /// <paramref name="queueId"/> — the "::" picker for one ticket.
    Task<IReadOnlyList<FormTemplateSummary>> ListAvailableForQueueAsync(Guid queueId, CancellationToken ct);
}

/// v0.1.52 — Dapper repository for <c>form_templates</c>.
public sealed class FormTemplateRepository : IFormTemplateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public FormTemplateRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SummarySelect = """
        SELECT id            AS Id,
               name          AS Name,
               description   AS Description,
               is_active     AS IsActive,
               queue_ids     AS QueueIds,
               field_count   AS FieldCount,
               created_utc   AS CreatedUtc,
               updated_utc   AS UpdatedUtc
        FROM form_templates
        """;

    public async Task<IReadOnlyList<FormTemplateSummary>> ListAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            SummarySelect + " ORDER BY is_active DESC, lower(name), id", cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<FormTemplateSummary>> ListAvailableForQueueAsync(Guid queueId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            SummarySelect + """
             WHERE is_active = TRUE
               AND (cardinality(queue_ids) = 0 OR @queueId = ANY(queue_ids))
             ORDER BY lower(name), id
            """, new { queueId }, cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public async Task<FormTemplateDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DetailRow>(new CommandDefinition("""
            SELECT id               AS Id,
                   name             AS Name,
                   description      AS Description,
                   is_active        AS IsActive,
                   queue_ids        AS QueueIds,
                   definition::text AS DefinitionJson,
                   field_count      AS FieldCount,
                   created_utc      AS CreatedUtc,
                   updated_utc      AS UpdatedUtc
            FROM form_templates
            WHERE id = @id
            """, new { id }, cancellationToken: ct));
        if (row is null) return null;
        return new FormTemplateDetail(
            row.Id, row.Name, row.Description, row.IsActive,
            row.QueueIds ?? Array.Empty<Guid>(),
            FormDefinition.Parse(row.DefinitionJson),
            row.FieldCount, row.CreatedUtc, row.UpdatedUtc);
    }

    public async Task<bool> ActiveNameTakenAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM form_templates
                WHERE is_active AND lower(name) = lower(@name)
                  AND (@exceptId::uuid IS NULL OR id <> @exceptId))
            """, new { name, exceptId }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(FormTemplateInput input, Guid? createdByUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO form_templates
                (name, description, is_active, queue_ids, definition, field_count, created_by_user_id)
            VALUES (@name, @description, @isActive, @queueIds, @definition::jsonb, @fieldCount, @createdBy)
            RETURNING id
            """, new
        {
            name = input.Name,
            description = input.Description,
            isActive = input.IsActive,
            queueIds = input.QueueIds.Distinct().ToArray(),
            definition = input.Definition.ToJson(),
            fieldCount = input.Definition.InputFieldCount,
            createdBy = createdByUserId,
        }, cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(Guid id, FormTemplateInput input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE form_templates
               SET name = @name,
                   description = @description,
                   is_active = @isActive,
                   queue_ids = @queueIds,
                   definition = @definition::jsonb,
                   field_count = @fieldCount,
                   updated_utc = now()
             WHERE id = @id
            """, new
        {
            id,
            name = input.Name,
            description = input.Description,
            isActive = input.IsActive,
            queueIds = input.QueueIds.Distinct().ToArray(),
            definition = input.Definition.ToJson(),
            fieldCount = input.Definition.InputFieldCount,
        }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // Filled forms keep their snapshot; the FK is ON DELETE SET NULL.
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM form_templates WHERE id = @id", new { id }, cancellationToken: ct));
        return n > 0;
    }

    private static FormTemplateSummary ToSummary(SummaryRow r) => new(
        r.Id, r.Name, r.Description, r.IsActive, r.QueueIds ?? Array.Empty<Guid>(),
        r.FieldCount, r.CreatedUtc, r.UpdatedUtc);

    private class SummaryRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public Guid[]? QueueIds { get; set; }
        public int FieldCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class DetailRow : SummaryRow
    {
        public string? DefinitionJson { get; set; }
    }
}
