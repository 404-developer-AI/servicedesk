using Dapper;
using Npgsql;
using Servicedesk.Domain.TicketTemplates;

namespace Servicedesk.Infrastructure.TicketTemplates;

public sealed class TicketTemplateRepository : ITicketTemplateRepository
{
    private const string SelectColumns = """
        id                    AS Id,
        name                  AS Name,
        description           AS Description,
        is_active             AS IsActive,
        subject               AS Subject,
        body_html             AS BodyHtml,
        initial_note_html     AS InitialNoteHtml,
        initial_note_internal AS InitialNoteInternal,
        queue_id              AS QueueId,
        priority_id           AS PriorityId,
        status_id             AS StatusId,
        category_id           AS CategoryId,
        ticket_type_id        AS TicketTypeId,
        assignee_user_id      AS AssigneeUserId,
        created_utc           AS CreatedUtc,
        updated_utc           AS UpdatedUtc,
        created_by            AS CreatedBy
        """;

    private readonly NpgsqlDataSource _dataSource;

    public TicketTemplateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<TicketTemplate>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM ticket_templates";
        if (!includeInactive) sql += " WHERE is_active = TRUE";
        sql += " ORDER BY is_active DESC, lower(name)";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<TicketTemplate?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM ticket_templates WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    public async Task<IReadOnlyList<TicketTemplate>> ListActiveAsync(CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM ticket_templates WHERE is_active = TRUE ORDER BY lower(name)";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<Guid> CreateAsync(TicketTemplate t, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO ticket_templates
                (name, description, is_active, subject, body_html, initial_note_html,
                 initial_note_internal, queue_id, priority_id, status_id, category_id,
                 ticket_type_id, assignee_user_id, created_by)
            VALUES
                (@Name, @Description, @IsActive, @Subject, @BodyHtml, @InitialNoteHtml,
                 @InitialNoteInternal, @QueueId, @PriorityId, @StatusId, @CategoryId,
                 @TicketTypeId, @AssigneeUserId, @CreatedBy)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, t, cancellationToken: ct));
    }

    public async Task UpdateAsync(TicketTemplate t, CancellationToken ct)
    {
        const string sql = """
            UPDATE ticket_templates
            SET name = @Name,
                description = @Description,
                is_active = @IsActive,
                subject = @Subject,
                body_html = @BodyHtml,
                initial_note_html = @InitialNoteHtml,
                initial_note_internal = @InitialNoteInternal,
                queue_id = @QueueId,
                priority_id = @PriorityId,
                status_id = @StatusId,
                category_id = @CategoryId,
                ticket_type_id = @TicketTypeId,
                assignee_user_id = @AssigneeUserId,
                updated_utc = now()
            WHERE id = @Id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, t, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM ticket_templates WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    private static TicketTemplate MapToDomain(Row r) => new(
        r.Id,
        r.Name,
        r.Description,
        r.IsActive,
        r.Subject,
        r.BodyHtml,
        r.InitialNoteHtml,
        r.InitialNoteInternal,
        r.QueueId,
        r.PriorityId,
        r.StatusId,
        r.CategoryId,
        r.TicketTypeId,
        r.AssigneeUserId,
        r.CreatedUtc,
        r.UpdatedUtc,
        r.CreatedBy);

    // Mutable class with public setters for Dapper column binding — see the
    // project memo on positional-record-struct null bugs.
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string BodyHtml { get; set; } = string.Empty;
        public string InitialNoteHtml { get; set; } = string.Empty;
        public bool InitialNoteInternal { get; set; }
        public Guid? QueueId { get; set; }
        public Guid? PriorityId { get; set; }
        public Guid? StatusId { get; set; }
        public Guid? CategoryId { get; set; }
        public Guid? TicketTypeId { get; set; }
        public Guid? AssigneeUserId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
    }
}
