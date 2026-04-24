using System.Data;
using Dapper;
using Npgsql;
using Servicedesk.Domain.IntakeForms;

namespace Servicedesk.Infrastructure.IntakeForms;

public sealed class IntakeTemplateRepository : IIntakeTemplateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public IntakeTemplateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<IntakeTemplate>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = """
            SELECT id AS Id, name AS Name, description AS Description, is_active AS IsActive,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, created_by AS CreatedBy
            FROM intake_templates
            """;
        if (!includeInactive) sql += " WHERE is_active = TRUE";
        sql += " ORDER BY is_active DESC, name";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TemplateRow>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return Array.Empty<IntakeTemplate>();

        var ids = rows.Select(r => r.Id).ToArray();
        var questions = await LoadQuestionsAsync(conn, ids, ct);

        return rows
            .Select(r => new IntakeTemplate(
                r.Id, r.Name, r.Description, r.IsActive, r.CreatedUtc, r.UpdatedUtc, r.CreatedBy,
                questions.TryGetValue(r.Id, out var qs) ? qs : Array.Empty<IntakeQuestion>()))
            .ToList();
    }

    public async Task<IntakeTemplate?> GetAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, description AS Description, is_active AS IsActive,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, created_by AS CreatedBy
            FROM intake_templates WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<TemplateRow>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        if (row is null) return null;

        var map = await LoadQuestionsAsync(conn, new[] { id }, ct);
        IReadOnlyList<IntakeQuestion> questions = map.TryGetValue(id, out var qs) ? qs : Array.Empty<IntakeQuestion>();
        return new IntakeTemplate(row.Id, row.Name, row.Description, row.IsActive,
            row.CreatedUtc, row.UpdatedUtc, row.CreatedBy, questions);
    }

    public async Task<Guid> CreateAsync(string name, string? description, IReadOnlyList<IntakeQuestionInput> questions, Guid? createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO intake_templates (name, description, created_by)
            VALUES (@name, @description, @createdBy)
            RETURNING id
            """, new { name, description, createdBy }, transaction: tx, cancellationToken: ct));

        await InsertQuestionsAsync(conn, tx, id, questions, ct);

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task UpdateAsync(Guid id, string name, string? description, bool isActive, IReadOnlyList<IntakeQuestionInput> questions, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE intake_templates
            SET name = @name, description = @description, is_active = @isActive, updated_utc = now()
            WHERE id = @id
            """, new { id, name, description, isActive }, transaction: tx, cancellationToken: ct));

        // Full-replace strategy (same as SLA business-hours slots). Safe
        // because answers reference question_ids with ON DELETE RESTRICT —
        // if any questions have live answers, the bulk-delete fails and the
        // transaction rolls back. Callers must catch that and refuse the
        // edit with a user-facing error.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM intake_template_questions WHERE template_id = @id",
            new { id }, transaction: tx, cancellationToken: ct));

        await InsertQuestionsAsync(conn, tx, id, questions, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE intake_templates SET is_active = FALSE, updated_utc = now() WHERE id = @id AND is_active = TRUE",
            new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> IsReferencedByInstancesAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM intake_form_instances WHERE template_id = @id",
            new { id }, cancellationToken: ct));
        return count > 0;
    }

    private static async Task InsertQuestionsAsync(NpgsqlConnection conn, IDbTransaction tx, Guid templateId, IReadOnlyList<IntakeQuestionInput> questions, CancellationToken ct)
    {
        foreach (var q in questions)
        {
            var questionId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO intake_template_questions
                    (template_id, sort_order, question_type, label, help_text,
                     is_required, default_value, default_token)
                VALUES (@templateId, @sortOrder, @questionType, @label, @helpText,
                        @isRequired, @defaultValue, @defaultToken)
                RETURNING id
                """,
                new
                {
                    templateId,
                    sortOrder = q.SortOrder,
                    questionType = q.Type.ToString(),
                    label = q.Label,
                    helpText = q.HelpText,
                    isRequired = q.IsRequired,
                    defaultValue = q.DefaultValue,
                    defaultToken = q.DefaultToken,
                },
                transaction: tx, cancellationToken: ct));

            if (q.Type is IntakeQuestionType.DropdownSingle or IntakeQuestionType.DropdownMulti)
            {
                foreach (var opt in q.Options)
                {
                    await conn.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO intake_template_question_options (question_id, sort_order, value, label)
                        VALUES (@questionId, @sortOrder, @value, @label)
                        """,
                        new { questionId, sortOrder = opt.SortOrder, value = opt.Value, label = opt.Label },
                        transaction: tx, cancellationToken: ct));
                }
            }
        }
    }

    private static async Task<Dictionary<Guid, List<IntakeQuestion>>> LoadQuestionsAsync(NpgsqlConnection conn, Guid[] templateIds, CancellationToken ct)
    {
        const string qSql = """
            SELECT id              AS Id,
                   template_id     AS TemplateId,
                   sort_order      AS SortOrder,
                   question_type   AS QuestionType,
                   label           AS Label,
                   help_text       AS HelpText,
                   is_required     AS IsRequired,
                   default_value   AS DefaultValue,
                   default_token   AS DefaultToken
            FROM intake_template_questions
            WHERE template_id = ANY(@ids)
            ORDER BY template_id, sort_order
            """;
        var qRows = (await conn.QueryAsync<QuestionRow>(new CommandDefinition(qSql, new { ids = templateIds }, cancellationToken: ct))).ToList();
        if (qRows.Count == 0) return new();

        var qIds = qRows.Select(q => q.Id).ToArray();
        var options = await LoadOptionsAsync(conn, qIds, ct);

        var map = new Dictionary<Guid, List<IntakeQuestion>>();
        foreach (var r in qRows)
        {
            if (!map.TryGetValue(r.TemplateId, out var list))
            {
                list = new();
                map[r.TemplateId] = list;
            }

            if (!Enum.TryParse<IntakeQuestionType>(r.QuestionType, ignoreCase: false, out var type))
            {
                // Hard fail — the DB CHECK constraint should make this
                // unreachable. If it happens, the enum drifted from the
                // constraint in a migration and we want to know loudly.
                throw new InvalidOperationException($"Unknown intake question type: {r.QuestionType}");
            }

            list.Add(new IntakeQuestion(
                r.Id, r.TemplateId, r.SortOrder, type, r.Label, r.HelpText,
                r.IsRequired, r.DefaultValue, r.DefaultToken,
                options.TryGetValue(r.Id, out var opts) ? opts : Array.Empty<IntakeQuestionOption>()));
        }

        return map;
    }

    private static async Task<Dictionary<long, List<IntakeQuestionOption>>> LoadOptionsAsync(NpgsqlConnection conn, long[] questionIds, CancellationToken ct)
    {
        const string sql = """
            SELECT id          AS Id,
                   question_id AS QuestionId,
                   sort_order  AS SortOrder,
                   value       AS Value,
                   label       AS Label
            FROM intake_template_question_options
            WHERE question_id = ANY(@ids)
            ORDER BY question_id, sort_order
            """;
        var rows = await conn.QueryAsync<OptionRow>(new CommandDefinition(sql, new { ids = questionIds }, cancellationToken: ct));

        var map = new Dictionary<long, List<IntakeQuestionOption>>();
        foreach (var r in rows)
        {
            if (!map.TryGetValue(r.QuestionId, out var list))
            {
                list = new();
                map[r.QuestionId] = list;
            }

            list.Add(new IntakeQuestionOption(r.Id, r.QuestionId, r.SortOrder, r.Value, r.Label));
        }

        return map;
    }

    // Plain classes instead of record structs — Dapper's ctor-based
    // mapping stumbles on nullable positional record structs (returns
    // null even when the row exists), which led to a hard-to-find NRE
    // where Create → GetAsync fired null into the endpoint response.
    // Mutable classes with public setters map cleanly via column name.
    private sealed class TemplateRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
    }

    private sealed class QuestionRow
    {
        public long Id { get; set; }
        public Guid TemplateId { get; set; }
        public int SortOrder { get; set; }
        public string QuestionType { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? HelpText { get; set; }
        public bool IsRequired { get; set; }
        public string? DefaultValue { get; set; }
        public string? DefaultToken { get; set; }
    }

    private sealed class OptionRow
    {
        public long Id { get; set; }
        public long QuestionId { get; set; }
        public int SortOrder { get; set; }
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }
}
