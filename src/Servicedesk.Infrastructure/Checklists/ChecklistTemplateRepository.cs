using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Checklists;

public interface IChecklistTemplateRepository
{
    Task<IReadOnlyList<ChecklistTemplateSummary>> ListAsync(CancellationToken ct);
    Task<ChecklistTemplateDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<Guid> CreateAsync(ChecklistTemplateInput input, Guid? createdByUserId, CancellationToken ct);
    Task<bool> UpdateAsync(Guid id, ChecklistTemplateInput input, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    /// Active templates whose queue scope is empty (all queues) or contains
    /// <paramref name="queueId"/> — the "attach" picker for one ticket.
    Task<IReadOnlyList<ChecklistTemplateSummary>> ListAvailableForQueueAsync(Guid queueId, CancellationToken ct);
}

public sealed class ChecklistTemplateRepository : IChecklistTemplateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ChecklistTemplateRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SummarySelect = """
        SELECT id            AS Id,
               name          AS Name,
               description   AS Description,
               is_active     AS IsActive,
               block_close   AS BlockClose,
               queue_ids     AS QueueIds,
               item_count    AS ItemCount,
               created_utc   AS CreatedUtc,
               updated_utc   AS UpdatedUtc
        FROM checklist_templates
        """;

    public async Task<IReadOnlyList<ChecklistTemplateSummary>> ListAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            SummarySelect + " ORDER BY is_active DESC, lower(name), id", cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<ChecklistTemplateSummary>> ListAvailableForQueueAsync(Guid queueId, CancellationToken ct)
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

    public async Task<ChecklistTemplateDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DetailRow>(new CommandDefinition("""
            SELECT id               AS Id,
                   name             AS Name,
                   description      AS Description,
                   is_active        AS IsActive,
                   block_close      AS BlockClose,
                   queue_ids        AS QueueIds,
                   definition::text AS DefinitionJson,
                   item_count       AS ItemCount,
                   created_utc      AS CreatedUtc,
                   updated_utc      AS UpdatedUtc
            FROM checklist_templates
            WHERE id = @id
            """, new { id }, cancellationToken: ct));
        if (row is null) return null;
        return new ChecklistTemplateDetail(
            row.Id, row.Name, row.Description, row.IsActive, row.BlockClose,
            row.QueueIds ?? Array.Empty<Guid>(),
            ChecklistTemplateDefinition.Parse(row.DefinitionJson),
            row.ItemCount, row.CreatedUtc, row.UpdatedUtc);
    }

    public async Task<Guid> CreateAsync(ChecklistTemplateInput input, Guid? createdByUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO checklist_templates
                (name, description, is_active, block_close, queue_ids, definition, item_count, search_text, created_by_user_id)
            VALUES (@name, @description, @isActive, @blockClose, @queueIds, @definition::jsonb, @itemCount, @searchText, @createdBy)
            RETURNING id
            """, new
        {
            name = input.Name,
            description = input.Description,
            isActive = input.IsActive,
            blockClose = input.BlockClose,
            queueIds = input.QueueIds.Distinct().ToArray(),
            definition = input.Definition.ToJson(),
            itemCount = input.Definition.ItemCount,
            searchText = BuildSearchText(input),
            createdBy = createdByUserId,
        }, cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(Guid id, ChecklistTemplateInput input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE checklist_templates
               SET name = @name,
                   description = @description,
                   is_active = @isActive,
                   block_close = @blockClose,
                   queue_ids = @queueIds,
                   definition = @definition::jsonb,
                   item_count = @itemCount,
                   search_text = @searchText,
                   updated_utc = now()
             WHERE id = @id
            """, new
        {
            id,
            name = input.Name,
            description = input.Description,
            isActive = input.IsActive,
            blockClose = input.BlockClose,
            queueIds = input.QueueIds.Distinct().ToArray(),
            definition = input.Definition.ToJson(),
            itemCount = input.Definition.ItemCount,
            searchText = BuildSearchText(input),
        }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // Attached checklists keep their snapshot; the FK is ON DELETE SET NULL.
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM checklist_templates WHERE id = @id", new { id }, cancellationToken: ct));
        return n > 0;
    }

    private static string BuildSearchText(ChecklistTemplateInput input)
        => input.Name + "\n" + input.Description + "\n" + input.Definition.FlattenForSearch();

    private static ChecklistTemplateSummary ToSummary(SummaryRow r) => new(
        r.Id, r.Name, r.Description, r.IsActive, r.BlockClose,
        r.QueueIds ?? Array.Empty<Guid>(), r.ItemCount, r.CreatedUtc, r.UpdatedUtc);

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool BlockClose { get; set; }
        public Guid[]? QueueIds { get; set; }
        public int ItemCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class DetailRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool BlockClose { get; set; }
        public Guid[]? QueueIds { get; set; }
        public string DefinitionJson { get; set; } = "{}";
        public int ItemCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
