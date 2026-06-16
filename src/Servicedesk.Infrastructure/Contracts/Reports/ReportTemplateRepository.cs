using Dapper;
using Npgsql;
using Servicedesk.Domain.Reports;

namespace Servicedesk.Infrastructure.Contracts.Reports;

public interface IReportTemplateRepository
{
    Task<IReadOnlyList<ReportTemplate>> ListAsync(string purpose, bool includeInactive, CancellationToken ct);
    Task<ReportTemplate?> GetAsync(Guid id, CancellationToken ct);

    Task<Guid> CreateAsync(
        string purpose, string name, string? description, string subject, string bodyHtml,
        Guid? queueId, IReadOnlyList<string> columns, string scope, bool attachPdf,
        Guid? createdBy, CancellationToken ct);

    Task UpdateAsync(
        Guid id, string name, string? description, string subject, string bodyHtml,
        Guid? queueId, IReadOnlyList<string> columns, string scope, bool attachPdf, bool isActive,
        CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public sealed class ReportTemplateRepository : IReportTemplateRepository
{
    private const string SelectColumns = """
        id          AS Id,
        purpose     AS Purpose,
        name        AS Name,
        description AS Description,
        subject     AS Subject,
        body_html   AS BodyHtml,
        queue_id    AS QueueId,
        columns     AS Columns,
        scope       AS Scope,
        attach_pdf  AS AttachPdf,
        is_active   AS IsActive,
        created_utc AS CreatedUtc,
        updated_utc AS UpdatedUtc,
        created_by  AS CreatedBy
        """;

    private readonly NpgsqlDataSource _dataSource;

    public ReportTemplateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ReportTemplate>> ListAsync(string purpose, bool includeInactive, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM report_templates WHERE purpose = @purpose";
        if (!includeInactive) sql += " AND is_active = TRUE";
        sql += " ORDER BY is_active DESC, lower(name)";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { purpose }, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<ReportTemplate?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM report_templates WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    public async Task<Guid> CreateAsync(
        string purpose, string name, string? description, string subject, string bodyHtml,
        Guid? queueId, IReadOnlyList<string> columns, string scope, bool attachPdf,
        Guid? createdBy, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO report_templates
                (purpose, name, description, subject, body_html, queue_id, columns, scope, attach_pdf, created_by)
            VALUES
                (@purpose, @name, @description, @subject, @bodyHtml, @queueId, @columns, @scope, @attachPdf, @createdBy)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                purpose,
                name,
                description,
                subject,
                bodyHtml,
                queueId,
                columns = columns.ToArray(),
                scope,
                attachPdf,
                createdBy,
            },
            cancellationToken: ct));
    }

    public async Task UpdateAsync(
        Guid id, string name, string? description, string subject, string bodyHtml,
        Guid? queueId, IReadOnlyList<string> columns, string scope, bool attachPdf, bool isActive,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE report_templates
            SET name = @name,
                description = @description,
                subject = @subject,
                body_html = @bodyHtml,
                queue_id = @queueId,
                columns = @columns,
                scope = @scope,
                attach_pdf = @attachPdf,
                is_active = @isActive,
                updated_utc = now()
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id,
                name,
                description,
                subject,
                bodyHtml,
                queueId,
                columns = columns.ToArray(),
                scope,
                attachPdf,
                isActive,
            },
            cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM report_templates WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    private static ReportTemplate MapToDomain(Row r) => new(
        r.Id,
        r.Purpose,
        r.Name,
        r.Description,
        r.Subject,
        r.BodyHtml,
        r.QueueId,
        r.Columns ?? Array.Empty<string>(),
        r.Scope,
        r.AttachPdf,
        r.IsActive,
        r.CreatedUtc,
        r.UpdatedUtc,
        r.CreatedBy);

    // Mutable class for Dapper column-name binding (avoids positional-record
    // null-binding pitfalls — see the project memo on Dapper row DTOs).
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string BodyHtml { get; set; } = string.Empty;
        public Guid? QueueId { get; set; }
        public string[]? Columns { get; set; }
        public string Scope { get; set; } = ReportScope.All;
        public bool AttachPdf { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
    }
}
