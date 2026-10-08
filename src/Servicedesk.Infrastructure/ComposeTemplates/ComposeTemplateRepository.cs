using Dapper;
using Npgsql;
using Servicedesk.Domain.ComposeTemplates;

namespace Servicedesk.Infrastructure.ComposeTemplates;

public sealed class ComposeTemplateRepository : IComposeTemplateRepository
{
    private const string SelectColumns = """
        id                  AS Id,
        name                AS Name,
        description         AS Description,
        body_html           AS BodyHtml,
        is_active           AS IsActive,
        queue_ids           AS QueueIds,
        status_ids          AS StatusIds,
        auto_insert_on_note AS AutoInsertOnNote,
        use_for_note        AS UseForNote,
        use_for_mail        AS UseForMail,
        use_for_call        AS UseForCall,
        auto_insert_on_call AS AutoInsertOnCall,
        created_utc         AS CreatedUtc,
        updated_utc         AS UpdatedUtc,
        created_by          AS CreatedBy,
        linked_survey_id    AS LinkedSurveyId,
        workflow_close      AS WorkflowClose,
        specialist_consult  AS SpecialistConsult
        """;

    private readonly NpgsqlDataSource _dataSource;

    public ComposeTemplateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SetWorkflowFlagsAsync(Guid id, bool workflowClose, bool specialistConsult, CancellationToken ct)
    {
        const string sql = """
            UPDATE compose_templates
            SET workflow_close = @workflowClose, specialist_consult = @specialistConsult
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workflowClose, specialistConsult }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ComposeTemplate>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM compose_templates";
        if (!includeInactive) sql += " WHERE is_active = TRUE";
        sql += " ORDER BY is_active DESC, lower(name)";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<ComposeTemplate?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM compose_templates WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    // v0.1.17 — kind → scope column. Constant strings only (never input).
    private static string KindColumn(ComposeTemplateKind kind) => kind switch
    {
        ComposeTemplateKind.Note => "use_for_note",
        ComposeTemplateKind.Mail => "use_for_mail",
        ComposeTemplateKind.Call => "use_for_call",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public async Task<IReadOnlyList<ComposeTemplate>> ListForQueueAsync(Guid? queueId, Guid? statusId, ComposeTemplateKind? kind, CancellationToken ct)
    {
        var kindFilter = kind is { } k ? $" AND {KindColumn(k)} = TRUE" : "";
        // queue_ids = '{}' means "any queue", status_ids = '{}' means "any
        // status". When the caller passes a concrete value we union both
        // buckets ("unrestricted OR explicitly contains the value"). When the
        // caller has no queueId at all (New-Ticket drawer) we only return
        // unrestricted templates because queue-scoped ones would be
        // misleading at that point. statusId is independent: null skips the
        // status filter entirely (preserves pre-v0.0.42 semantics).
        string sql;
        if (queueId is null)
        {
            sql = $"""
                  SELECT {SelectColumns}
                  FROM compose_templates
                  WHERE is_active = TRUE
                    AND cardinality(queue_ids) = 0
                    AND (@statusId::uuid IS NULL OR cardinality(status_ids) = 0 OR @statusId = ANY(status_ids)){kindFilter}
                  ORDER BY lower(name)
                  """;
        }
        else
        {
            sql = $"""
                  SELECT {SelectColumns}
                  FROM compose_templates
                  WHERE is_active = TRUE
                    AND (cardinality(queue_ids) = 0 OR @queueId = ANY(queue_ids))
                    AND (@statusId::uuid IS NULL OR cardinality(status_ids) = 0 OR @statusId = ANY(status_ids)){kindFilter}
                  ORDER BY lower(name)
                  """;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { queueId, statusId }, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<ComposeTemplate?> FindAutoInsertAsync(Guid queueId, Guid statusId, ComposeTemplateKind kind, CancellationToken ct)
    {
        var autoColumn = kind switch
        {
            ComposeTemplateKind.Note => "auto_insert_on_note",
            ComposeTemplateKind.Call => "auto_insert_on_call",
            _ => null,
        };
        if (autoColumn is null) return null;
        // Picks the single best-matching auto-insert template for a
        // (queue, status) tuple. Tie-breaker is most-recently-updated, so
        // an admin editing a template makes it "win" deterministically.
        var sql = $"""
            SELECT {SelectColumns}
            FROM compose_templates
            WHERE is_active = TRUE
              AND {autoColumn} = TRUE
              AND {KindColumn(kind)} = TRUE
              AND (cardinality(queue_ids) = 0 OR @queueId = ANY(queue_ids))
              AND (cardinality(status_ids) = 0 OR @statusId = ANY(status_ids))
            ORDER BY updated_utc DESC
            LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { queueId, statusId }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    public async Task<Guid> CreateAsync(
        string name,
        string? description,
        string bodyHtml,
        IReadOnlyList<Guid> queueIds,
        IReadOnlyList<Guid> statusIds,
        bool autoInsertOnNote,
        Guid? linkedSurveyId,
        Guid? createdBy,
        ComposeTemplateKindScope kinds,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO compose_templates
                (name, description, body_html, queue_ids, status_ids, auto_insert_on_note, linked_survey_id, created_by,
                 use_for_note, use_for_mail, use_for_call, auto_insert_on_call)
            VALUES
                (@name, @description, @bodyHtml, @queueIds, @statusIds, @autoInsertOnNote, @linkedSurveyId, @createdBy,
                 @useForNote, @useForMail, @useForCall, @autoInsertOnCall)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                name,
                description,
                bodyHtml,
                queueIds = queueIds.ToArray(),
                statusIds = statusIds.ToArray(),
                autoInsertOnNote,
                linkedSurveyId,
                createdBy,
                useForNote = kinds.UseForNote,
                useForMail = kinds.UseForMail,
                useForCall = kinds.UseForCall,
                autoInsertOnCall = kinds.AutoInsertOnCall,
            },
            cancellationToken: ct));
    }

    public async Task UpdateAsync(
        Guid id,
        string name,
        string? description,
        string bodyHtml,
        bool isActive,
        IReadOnlyList<Guid> queueIds,
        IReadOnlyList<Guid> statusIds,
        bool autoInsertOnNote,
        Guid? linkedSurveyId,
        ComposeTemplateKindScope kinds,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE compose_templates
            SET name = @name,
                description = @description,
                body_html = @bodyHtml,
                is_active = @isActive,
                queue_ids = @queueIds,
                status_ids = @statusIds,
                auto_insert_on_note = @autoInsertOnNote,
                linked_survey_id = @linkedSurveyId,
                use_for_note = @useForNote,
                use_for_mail = @useForMail,
                use_for_call = @useForCall,
                auto_insert_on_call = @autoInsertOnCall,
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
                bodyHtml,
                isActive,
                queueIds = queueIds.ToArray(),
                statusIds = statusIds.ToArray(),
                autoInsertOnNote,
                linkedSurveyId,
                useForNote = kinds.UseForNote,
                useForMail = kinds.UseForMail,
                useForCall = kinds.UseForCall,
                autoInsertOnCall = kinds.AutoInsertOnCall,
            },
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
        r.CreatedBy,
        r.LinkedSurveyId,
        r.StatusIds ?? Array.Empty<Guid>(),
        r.AutoInsertOnNote,
        r.UseForNote,
        r.UseForMail,
        r.UseForCall,
        r.AutoInsertOnCall,
        r.WorkflowClose,
        r.SpecialistConsult);

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
        public Guid[]? StatusIds { get; set; }
        public bool AutoInsertOnNote { get; set; }
        public bool UseForNote { get; set; } = true;
        public bool UseForMail { get; set; } = true;
        public bool UseForCall { get; set; } = true;
        public bool AutoInsertOnCall { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
        public Guid? LinkedSurveyId { get; set; }
        public bool WorkflowClose { get; set; }
        public bool SpecialistConsult { get; set; }
    }
}
