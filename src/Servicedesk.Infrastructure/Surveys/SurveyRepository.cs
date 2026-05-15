using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Surveys;

namespace Servicedesk.Infrastructure.Surveys;

public sealed class SurveyRepository : ISurveyRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SurveyRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<SurveySummary>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = """
            SELECT s.id                AS Id,
                   s.name              AS Name,
                   s.description       AS Description,
                   s.is_active         AS IsActive,
                   s.ttl_days          AS TtlDays,
                   COALESCE((SELECT count(*) FROM survey_questions q WHERE q.survey_id = s.id AND q.applies_to = 'Survey'), 0)::int AS QuestionCount,
                   COALESCE((SELECT count(*) FROM survey_questions q WHERE q.survey_id = s.id AND q.applies_to = 'Agent'), 0)::int AS AgentQuestionCount,
                   COALESCE((SELECT count(*) FROM survey_invitations i WHERE i.survey_id = s.id), 0)::int AS InvitationCount,
                   COALESCE((SELECT count(*) FROM survey_invitations i
                             JOIN survey_responses r ON r.invitation_id = i.id
                             WHERE i.survey_id = s.id), 0)::int AS ResponseCount,
                   s.created_utc       AS CreatedUtc,
                   s.updated_utc       AS UpdatedUtc
            FROM surveys s
            """;
        if (!includeInactive) sql += " WHERE s.is_active = TRUE";
        sql += " ORDER BY s.is_active DESC, lower(s.name)";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new SurveySummary(
            r.Id, r.Name, r.Description, r.IsActive, r.TtlDays,
            r.QuestionCount, r.AgentQuestionCount,
            r.InvitationCount, r.ResponseCount,
            r.CreatedUtc, r.UpdatedUtc)).ToList();
    }

    public Task<Survey?> GetAsync(Guid id, CancellationToken ct) => GetCoreAsync(id, requireActive: false, ct);

    public Task<Survey?> GetActiveAsync(Guid id, CancellationToken ct) => GetCoreAsync(id, requireActive: true, ct);

    private async Task<Survey?> GetCoreAsync(Guid id, bool requireActive, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var head = await conn.QueryFirstOrDefaultAsync<HeadRow>(new CommandDefinition(
            """
            SELECT id                  AS Id,
                   name                AS Name,
                   description         AS Description,
                   intro_html          AS IntroHtml,
                   invite_subject      AS InviteSubject,
                   invite_body_html    AS InviteBodyHtml,
                   is_active           AS IsActive,
                   ttl_days            AS TtlDays,
                   agent_block_heading AS AgentBlockHeading,
                   submit_button_label AS SubmitButtonLabel,
                   thank_you_message   AS ThankYouMessage,
                   expired_message     AS ExpiredMessage,
                   not_found_message   AS NotFoundMessage,
                   created_utc         AS CreatedUtc,
                   updated_utc         AS UpdatedUtc,
                   created_by          AS CreatedBy
            FROM surveys WHERE id = @id
            """, new { id }, cancellationToken: ct));
        if (head is null) return null;
        if (requireActive && !head.IsActive) return null;

        var questions = await LoadQuestionsAsync(conn, id, ct);

        return new Survey(
            head.Id, head.Name, head.Description, head.IntroHtml,
            head.InviteSubject, head.InviteBodyHtml, head.IsActive,
            head.TtlDays,
            head.AgentBlockHeading,
            head.SubmitButtonLabel ?? string.Empty,
            head.ThankYouMessage ?? string.Empty,
            head.ExpiredMessage ?? string.Empty,
            head.NotFoundMessage ?? string.Empty,
            head.CreatedUtc, head.UpdatedUtc, head.CreatedBy, questions);
    }

    public async Task<Guid> CreateAsync(SurveyMetadataInput metadata, Guid? createdBy, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO surveys (name, description, intro_html, invite_subject, invite_body_html,
                                 ttl_days, agent_block_heading, submit_button_label,
                                 thank_you_message, expired_message, not_found_message,
                                 created_by)
            VALUES (@name, @description, @introHtml, @inviteSubject, @inviteBodyHtml,
                    @ttlDays, @agentBlockHeading, @submitButtonLabel,
                    @thankYouMessage, @expiredMessage, @notFoundMessage,
                    @createdBy)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            name = metadata.Name,
            description = metadata.Description,
            introHtml = metadata.IntroHtml,
            inviteSubject = metadata.InviteSubject,
            inviteBodyHtml = metadata.InviteBodyHtml,
            ttlDays = metadata.TtlDays,
            agentBlockHeading = metadata.AgentBlockHeading,
            submitButtonLabel = metadata.SubmitButtonLabel,
            thankYouMessage = metadata.ThankYouMessage,
            expiredMessage = metadata.ExpiredMessage,
            notFoundMessage = metadata.NotFoundMessage,
            createdBy,
        }, cancellationToken: ct));
    }

    public async Task UpdateMetadataAsync(Guid id, SurveyMetadataInput metadata, bool isActive, CancellationToken ct)
    {
        const string sql = """
            UPDATE surveys
            SET name                = @name,
                description         = @description,
                intro_html          = @introHtml,
                invite_subject      = @inviteSubject,
                invite_body_html    = @inviteBodyHtml,
                is_active           = @isActive,
                ttl_days            = @ttlDays,
                agent_block_heading = @agentBlockHeading,
                submit_button_label = @submitButtonLabel,
                thank_you_message   = @thankYouMessage,
                expired_message     = @expiredMessage,
                not_found_message   = @notFoundMessage,
                updated_utc         = now()
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            id,
            name = metadata.Name,
            description = metadata.Description,
            introHtml = metadata.IntroHtml,
            inviteSubject = metadata.InviteSubject,
            inviteBodyHtml = metadata.InviteBodyHtml,
            isActive,
            ttlDays = metadata.TtlDays,
            agentBlockHeading = metadata.AgentBlockHeading,
            submitButtonLabel = metadata.SubmitButtonLabel,
            thankYouMessage = metadata.ThankYouMessage,
            expiredMessage = metadata.ExpiredMessage,
            notFoundMessage = metadata.NotFoundMessage,
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> ReplaceQuestionsAsync(
        Guid surveyId,
        IReadOnlyList<SurveyQuestionInput> questions,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM survey_questions WHERE survey_id = @surveyId",
            new { surveyId }, transaction: tx, cancellationToken: ct));

        // updated_utc bumps even on a question-only edit so the designer's
        // "Last saved" indicator and the survey listing stay in sync.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE surveys SET updated_utc = now() WHERE id = @surveyId",
            new { surveyId }, transaction: tx, cancellationToken: ct));

        var ids = new List<long>(questions.Count);
        foreach (var q in questions)
        {
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO survey_questions (survey_id, sort_order, question_type, applies_to, label, help_text, is_required, config_json)
                VALUES (@surveyId, @sortOrder, @type, @appliesTo, @label, @helpText, @isRequired, @configJson::jsonb)
                RETURNING id
                """, new
                {
                    surveyId,
                    sortOrder = q.SortOrder,
                    type = q.Type.ToString(),
                    appliesTo = q.AppliesTo.ToString(),
                    label = q.Label,
                    helpText = q.HelpText,
                    isRequired = q.IsRequired,
                    configJson = string.IsNullOrWhiteSpace(q.ConfigJson) ? "{}" : q.ConfigJson,
                }, transaction: tx, cancellationToken: ct));
            ids.Add(id);
        }

        await tx.CommitAsync(ct);
        return ids;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        // Block hard-delete when responses exist; surfaces as a 409 in the
        // endpoint so the admin sees a clear "deactivate instead" message
        // rather than a silent FK cascade orphaning agent leaderboards.
        if (await HasResponsesAsync(id, ct)) return false;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM surveys WHERE id = @id", new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> HasResponsesAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM survey_invitations i
                JOIN survey_responses r ON r.invitation_id = i.id
                WHERE i.survey_id = @id
            )
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    internal static async Task<IReadOnlyList<SurveyQuestion>> LoadQuestionsAsync(NpgsqlConnection conn, Guid surveyId, CancellationToken ct)
    {
        const string sql = """
            SELECT id                    AS Id,
                   survey_id             AS SurveyId,
                   sort_order            AS SortOrder,
                   question_type         AS QuestionType,
                   applies_to            AS AppliesTo,
                   label                 AS Label,
                   help_text             AS HelpText,
                   is_required           AS IsRequired,
                   config_json::text     AS ConfigJson
            FROM survey_questions
            WHERE survey_id = @surveyId
            ORDER BY applies_to, sort_order
            """;
        var rows = await conn.QueryAsync<QuestionRow>(new CommandDefinition(sql, new { surveyId }, cancellationToken: ct));
        return rows.Select(q => new SurveyQuestion(
            q.Id, q.SurveyId, q.SortOrder,
            Enum.Parse<SurveyQuestionType>(q.QuestionType, ignoreCase: false),
            Enum.Parse<SurveyQuestionScope>(q.AppliesTo, ignoreCase: false),
            q.Label, q.HelpText, q.IsRequired,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(q.ConfigJson) ? "{}" : q.ConfigJson))).ToList();
    }

    // --- row DTOs (mutable per project memory on Dapper record-struct bug) ---

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public int? TtlDays { get; set; }
        public int QuestionCount { get; set; }
        public int AgentQuestionCount { get; set; }
        public int InvitationCount { get; set; }
        public int ResponseCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class HeadRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string IntroHtml { get; set; } = string.Empty;
        public string InviteSubject { get; set; } = string.Empty;
        public string InviteBodyHtml { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int? TtlDays { get; set; }
        public string? AgentBlockHeading { get; set; }
        public string? SubmitButtonLabel { get; set; }
        public string? ThankYouMessage { get; set; }
        public string? ExpiredMessage { get; set; }
        public string? NotFoundMessage { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? CreatedBy { get; set; }
    }

    private sealed class QuestionRow
    {
        public long Id { get; set; }
        public Guid SurveyId { get; set; }
        public int SortOrder { get; set; }
        public string QuestionType { get; set; } = "Text";
        public string AppliesTo { get; set; } = "Survey";
        public string Label { get; set; } = string.Empty;
        public string? HelpText { get; set; }
        public bool IsRequired { get; set; }
        public string? ConfigJson { get; set; }
    }
}
