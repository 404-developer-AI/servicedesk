using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.IntakeForms;

namespace Servicedesk.Infrastructure.IntakeForms;

public sealed class IntakeFormRepository : IIntakeFormRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public IntakeFormRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Guid> CreateDraftAsync(Guid ticketId, Guid templateId, string prefillJson, Guid? createdBy, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO intake_form_instances (ticket_id, template_id, status, prefill_json, created_by)
            VALUES (@ticketId, @templateId, 'Draft', @prefillJson::jsonb, @createdBy)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { ticketId, templateId, prefillJson, createdBy },
            cancellationToken: ct));
    }

    public async Task<bool> UpdateDraftPrefillAsync(Guid instanceId, Guid ticketId, string prefillJson, CancellationToken ct)
    {
        const string sql = """
            UPDATE intake_form_instances
            SET prefill_json = @prefillJson::jsonb
            WHERE id = @instanceId AND ticket_id = @ticketId AND status = 'Draft'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { instanceId, ticketId, prefillJson }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteDraftAsync(Guid instanceId, Guid ticketId, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM intake_form_instances
            WHERE id = @instanceId AND ticket_id = @ticketId AND status = 'Draft'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { instanceId, ticketId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<IntakeFormInstanceSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        const string sql = """
            SELECT i.id               AS Id,
                   i.template_id      AS TemplateId,
                   t.name             AS TemplateName,
                   i.status           AS Status,
                   i.expires_utc      AS ExpiresUtc,
                   i.created_utc      AS CreatedUtc,
                   i.sent_utc         AS SentUtc,
                   i.submitted_utc    AS SubmittedUtc,
                   i.sent_to_email    AS SentToEmail
            FROM intake_form_instances i
            JOIN intake_templates t ON t.id = i.template_id
            WHERE i.ticket_id = @ticketId
            ORDER BY i.created_utc DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
        return rows.Select(r => new IntakeFormInstanceSummary(
            r.Id, r.TemplateId, r.TemplateName,
            ParseStatus(r.Status), r.ExpiresUtc, r.CreatedUtc, r.SentUtc, r.SubmittedUtc, r.SentToEmail)).ToList();
    }

    public async Task<IntakeFormAgentView?> GetAgentViewAsync(Guid ticketId, Guid instanceId, CancellationToken ct)
    {
        const string instanceSql = """
            SELECT id                          AS Id,
                   template_id                 AS TemplateId,
                   ticket_id                   AS TicketId,
                   sent_event_id               AS SentEventId,
                   submitted_event_id          AS SubmittedEventId,
                   status                      AS Status,
                   expires_utc                 AS ExpiresUtc,
                   created_utc                 AS CreatedUtc,
                   sent_utc                    AS SentUtc,
                   submitted_utc               AS SubmittedUtc,
                   submitter_ip::text          AS SubmitterIp,
                   submitter_ua                AS SubmitterUa,
                   created_by                  AS CreatedBy,
                   sent_to_email               AS SentToEmail,
                   prefill_json::text          AS PrefillJson,
                   template_snapshot_json::text AS TemplateSnapshotJson
            FROM intake_form_instances
            WHERE id = @instanceId AND ticket_id = @ticketId
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<InstanceRow>(
            new CommandDefinition(instanceSql, new { instanceId, ticketId }, cancellationToken: ct));
        if (row is null) return null;

        // Draft instances render against the live template (admin may still
        // be tweaking defaults between create and send). Everything else
        // renders against the snapshot frozen at send time — so a later
        // template edit can never reshape a mail that's already on the
        // customer's screen. Fallback to live template covers rows written
        // before the snapshot column existed and not yet backfilled.
        var status = ParseStatus(row.Status);
        IntakeTemplate? template = null;
        if (status != IntakeFormStatus.Draft && !string.IsNullOrEmpty(row.TemplateSnapshotJson))
        {
            template = DeserializeTemplateSnapshot(row.TemplateId, row.TemplateSnapshotJson);
        }
        template ??= await LoadTemplateForViewAsync(conn, row.TemplateId, ct);
        if (template is null) return null;

        var instance = MapInstance(row);

        // For Submitted instances, surface the answers so the agent UI can
        // render the completed form read-only next to the timeline event.
        JsonDocument? answers = null;
        if (instance.Status == IntakeFormStatus.Submitted)
        {
            answers = await LoadAnswersAsync(conn, instanceId, ct);
        }

        return new IntakeFormAgentView(instance, template, answers);
    }

    private static async Task<JsonDocument?> LoadAnswersAsync(NpgsqlConnection conn, Guid instanceId, CancellationToken ct)
    {
        const string sql = """
            SELECT question_id::text AS QuestionId, answer_json::text AS AnswerJson
            FROM intake_form_answers
            WHERE instance_id = @instanceId
            """;
        var rows = (await conn.QueryAsync<AnswerRow>(
            new CommandDefinition(sql, new { instanceId }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return null;

        // Build a JSON object `{ "42": <parsed answer>, "43": ... }`. Keep
        // the keys as strings so the frontend can index directly by
        // question id without number-coercion hassles.
        var dict = new Dictionary<string, JsonElement>(rows.Count);
        foreach (var r in rows)
        {
            using var doc = JsonDocument.Parse(r.AnswerJson);
            dict[r.QuestionId] = doc.RootElement.Clone();
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(dict));
    }

    public async Task<long?> SendDraftAsync(
        Guid instanceId,
        Guid ticketId,
        Guid actorUserId,
        byte[] tokenHash,
        byte[] tokenCipher,
        DateTime expiresUtc,
        string sentToEmail,
        string metadataJson,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Promote-guard: only promote a Draft whose template is still active.
        // Reading the active-flag here closes the TOCTOU with a concurrent
        // DeactivateAsync from the settings page.
        var ready = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM intake_form_instances i
                JOIN intake_templates t ON t.id = i.template_id
                WHERE i.id = @instanceId AND i.ticket_id = @ticketId
                  AND i.status = 'Draft' AND t.is_active = TRUE
            )
            """, new { instanceId, ticketId }, transaction: tx, cancellationToken: ct));
        if (!ready)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // Freeze the template shape onto the instance. From this point the
        // admin can edit the live template freely — this instance (and any
        // future submission against it) always resolves its questions +
        // options from the snapshot.
        var templateIdRow = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT template_id FROM intake_form_instances WHERE id = @instanceId",
            new { instanceId }, transaction: tx, cancellationToken: ct));
        var liveTemplate = await LoadTemplateForViewAsync(conn, templateIdRow, ct, tx);
        var snapshotJson = liveTemplate is null
            ? "{}"
            : SerializeTemplateSnapshot(liveTemplate);

        var sentEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'IntakeFormSent', @actorUserId, @metadataJson::jsonb, FALSE)
            RETURNING id
            """, new { ticketId, actorUserId, metadataJson }, transaction: tx, cancellationToken: ct));

        var now = DateTime.UtcNow;

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE intake_form_instances
            SET status = 'Sent',
                token_hash = @tokenHash,
                token_cipher = @tokenCipher,
                expires_utc = @expiresUtc,
                sent_utc = @now,
                sent_event_id = @sentEventId,
                sent_to_email = @sentToEmail,
                template_snapshot_json = @snapshotJson::jsonb
            WHERE id = @instanceId AND ticket_id = @ticketId AND status = 'Draft'
            """,
            new { instanceId, ticketId, tokenHash, tokenCipher, expiresUtc, now, sentEventId, sentToEmail, snapshotJson },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return sentEventId;
    }

    public async Task<bool> CancelSentAsync(Guid instanceId, Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        const string sql = """
            UPDATE intake_form_instances
            SET status = 'Cancelled'
            WHERE id = @instanceId AND ticket_id = @ticketId AND status = 'Sent'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { instanceId, ticketId }, cancellationToken: ct));
        _ = actorUserId; // Reserved for a future 'IntakeFormCancelled' event if we decide to audit this on the ticket timeline.
        return rows > 0;
    }

    public async Task<IntakePublicView?> GetByTokenHashForPublicAsync(byte[] tokenHash, CancellationToken ct)
    {
        // Public view always resolves from the snapshot — we only return
        // Sent/Submitted/Expired rows here and by then the instance has
        // been frozen. Template-name still comes from the live templates
        // table because the snapshot lives inline on the instance and
        // admins may rename a template cosmetically without wanting the
        // link already in someone's inbox to render stale.
        const string sql = """
            SELECT i.id                             AS InstanceId,
                   i.template_id                    AS TemplateId,
                   t.name                           AS TemplateName,
                   t.description                    AS TemplateDescription,
                   i.status                         AS Status,
                   i.expires_utc                    AS ExpiresUtc,
                   i.prefill_json::text             AS PrefillJson,
                   i.template_snapshot_json::text   AS TemplateSnapshotJson
            FROM intake_form_instances i
            JOIN intake_templates t ON t.id = i.template_id
            WHERE i.token_hash = @tokenHash AND i.status IN ('Sent','Submitted','Expired')
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<PublicRow>(
            new CommandDefinition(sql, new { tokenHash }, cancellationToken: ct));
        if (row is null) return null;

        IReadOnlyList<IntakeQuestion> questions;
        if (!string.IsNullOrEmpty(row.TemplateSnapshotJson))
        {
            var snap = DeserializeTemplateSnapshot(row.TemplateId, row.TemplateSnapshotJson);
            questions = snap.Questions;
        }
        else
        {
            // Legacy row without a snapshot — fall back to the live
            // template. The backfill in DatabaseBootstrapper fills these
            // on next boot so this path is a belt-and-braces fallback.
            questions = await LoadQuestionsForTemplateAsync(conn, row.TemplateId, ct);
        }

        return new IntakePublicView(
            row.InstanceId, row.TemplateId, row.TemplateName, row.TemplateDescription,
            ParseStatus(row.Status), row.ExpiresUtc,
            JsonDocument.Parse(row.PrefillJson),
            questions);
    }

    public async Task<SubmitResult?> TrySubmitAsync(
        byte[] tokenHash,
        IReadOnlyList<IntakeFormSubmitAnswer> answers,
        string? ip,
        string? userAgent,
        DateTime nowUtc,
        bool autoPin,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Atomic gate: returns the row only when status=Sent AND not expired.
        // SELECT FOR UPDATE locks the row so concurrent submit attempts see
        // our UPDATE in step 4 and pick up the 'Submitted' state, returning 0
        // affected and triggering the 409 branch. SentByUserId comes from the
        // IntakeFormSent event so the auto-pin (when enabled) can attribute
        // the pin to the agent who actually sent the link to the customer —
        // pinned_by_user_id is NOT NULL so we need a real user id here.
        var lookup = await conn.QueryFirstOrDefaultAsync<SubmitLookupRow>(new CommandDefinition("""
            SELECT i.id AS InstanceId, i.ticket_id AS TicketId, i.template_id AS TemplateId,
                   t.name AS TemplateName, i.expires_utc AS ExpiresUtc,
                   se.author_user_id AS SentByUserId
            FROM intake_form_instances i
            JOIN intake_templates t ON t.id = i.template_id
            LEFT JOIN ticket_events se ON se.id = i.sent_event_id
            WHERE i.token_hash = @tokenHash AND i.status = 'Sent'
            FOR UPDATE OF i
            """, new { tokenHash }, transaction: tx, cancellationToken: ct));

        if (lookup is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        if (lookup.ExpiresUtc is DateTime exp && exp <= nowUtc)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // Emit the IntakeFormSubmitted event. No body; metadata captures
        // instanceId + templateId + answer-count so timeline rendering can
        // summarise without loading every answer row.
        var metadataJson = JsonSerializer.Serialize(new
        {
            instanceId = lookup.InstanceId,
            templateId = lookup.TemplateId,
            templateName = lookup.TemplateName,
            answerCount = answers.Count,
        });

        var submittedEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, body_text, body_html, metadata, is_internal)
            VALUES (@ticketId, 'IntakeFormSubmitted', NULL, NULL, @metadataJson::jsonb, FALSE)
            RETURNING id
            """,
            new { ticketId = lookup.TicketId, metadataJson },
            transaction: tx, cancellationToken: ct));

        foreach (var a in answers)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO intake_form_answers (instance_id, question_id, answer_json)
                VALUES (@instanceId, @questionId, @answerJson::jsonb)
                """,
                new { instanceId = lookup.InstanceId, questionId = a.QuestionId, answerJson = a.AnswerJson },
                transaction: tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE intake_form_instances
            SET status = 'Submitted',
                submitted_utc = @nowUtc,
                submitter_ip = CASE WHEN @ip IS NULL THEN NULL ELSE @ip::inet END,
                submitter_ua = @userAgent,
                submitted_event_id = @submittedEventId
            WHERE id = @instanceId AND status = 'Sent'
            """,
            new { instanceId = lookup.InstanceId, nowUtc, ip, userAgent, submittedEventId },
            transaction: tx, cancellationToken: ct));

        // Auto-pin in the same transaction. Skipped silently when SentByUserId
        // is null (legacy rows where sent_event_id was never recorded) — the
        // submission itself still lands; only the auto-pin is best-effort.
        if (autoPin && lookup.SentByUserId is Guid pinnedBy)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ticket_event_pins (event_id, ticket_id, pinned_by_user_id, remark)
                VALUES (@eventId, @ticketId, @pinnedBy, '')
                ON CONFLICT (event_id) DO NOTHING
                """,
                new { eventId = submittedEventId, ticketId = lookup.TicketId, pinnedBy },
                transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);

        return new SubmitResult(
            lookup.InstanceId, lookup.TicketId, lookup.TemplateId,
            lookup.TemplateName, submittedEventId);
    }

    public async Task<IReadOnlyList<ExpiredInstance>> ExpireStaleAsync(int maxBatch, DateTime nowUtc, CancellationToken ct)
    {
        if (maxBatch <= 0) return Array.Empty<ExpiredInstance>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var stale = (await conn.QueryAsync<ExpireLookupRow>(new CommandDefinition("""
            SELECT i.id AS InstanceId, i.ticket_id AS TicketId, i.template_id AS TemplateId, t.name AS TemplateName
            FROM intake_form_instances i
            JOIN intake_templates t ON t.id = i.template_id
            WHERE i.status = 'Sent' AND i.expires_utc IS NOT NULL AND i.expires_utc <= @nowUtc
            ORDER BY i.expires_utc
            LIMIT @maxBatch
            FOR UPDATE OF i SKIP LOCKED
            """,
            new { nowUtc, maxBatch }, transaction: tx, cancellationToken: ct))).ToList();

        if (stale.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return Array.Empty<ExpiredInstance>();
        }

        var result = new List<ExpiredInstance>(stale.Count);

        foreach (var s in stale)
        {
            var metadataJson = JsonSerializer.Serialize(new
            {
                instanceId = s.InstanceId,
                templateId = s.TemplateId,
                templateName = s.TemplateName,
            });

            var expiredEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO ticket_events (ticket_id, event_type, metadata, is_internal)
                VALUES (@ticketId, 'IntakeFormExpired', @metadataJson::jsonb, FALSE)
                RETURNING id
                """,
                new { ticketId = s.TicketId, metadataJson },
                transaction: tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE intake_form_instances
                SET status = 'Expired'
                WHERE id = @instanceId AND status = 'Sent'
                """,
                new { instanceId = s.InstanceId }, transaction: tx, cancellationToken: ct));

            result.Add(new ExpiredInstance(s.InstanceId, s.TicketId, s.TemplateId, s.TemplateName, expiredEventId));
        }

        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task<IntakeTemplate?> LoadTemplateForViewAsync(NpgsqlConnection conn, Guid templateId, CancellationToken ct, System.Data.IDbTransaction? tx = null)
    {
        const string tSql = """
            SELECT id           AS Id,
                   name         AS Name,
                   description  AS Description,
                   is_active    AS IsActive,
                   created_utc  AS CreatedUtc,
                   updated_utc  AS UpdatedUtc,
                   created_by   AS CreatedBy
            FROM intake_templates WHERE id = @templateId
            """;
        var t = await conn.QueryFirstOrDefaultAsync<TemplateRow>(
            new CommandDefinition(tSql, new { templateId }, transaction: tx, cancellationToken: ct));
        if (t is null) return null;

        var questions = await LoadQuestionsForTemplateAsync(conn, templateId, ct, tx);
        return new IntakeTemplate(
            t.Id, t.Name, t.Description, t.IsActive,
            t.CreatedUtc, t.UpdatedUtc, t.CreatedBy, questions);
    }

    private static async Task<IReadOnlyList<IntakeQuestion>> LoadQuestionsForTemplateAsync(NpgsqlConnection conn, Guid templateId, CancellationToken ct, System.Data.IDbTransaction? tx = null)
    {
        const string qSql = """
            SELECT id             AS Id,
                   template_id    AS TemplateId,
                   sort_order     AS SortOrder,
                   question_type  AS QuestionType,
                   label          AS Label,
                   help_text      AS HelpText,
                   is_required    AS IsRequired,
                   default_value  AS DefaultValue,
                   default_token  AS DefaultToken
            FROM intake_template_questions
            WHERE template_id = @templateId
            ORDER BY sort_order
            """;
        var qRows = (await conn.QueryAsync<QuestionRow>(
            new CommandDefinition(qSql, new { templateId }, transaction: tx, cancellationToken: ct))).ToList();
        if (qRows.Count == 0) return Array.Empty<IntakeQuestion>();

        var qIds = qRows.Select(q => q.Id).ToArray();
        const string oSql = """
            SELECT id          AS Id,
                   question_id AS QuestionId,
                   sort_order  AS SortOrder,
                   value       AS Value,
                   label       AS Label
            FROM intake_template_question_options
            WHERE question_id = ANY(@ids)
            ORDER BY question_id, sort_order
            """;
        var oRows = (await conn.QueryAsync<OptionRow>(
            new CommandDefinition(oSql, new { ids = qIds }, transaction: tx, cancellationToken: ct))).ToList();

        var optsByQuestion = oRows
            .GroupBy(o => o.QuestionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IntakeQuestionOption>)g
                .Select(o => new IntakeQuestionOption(o.Id, o.QuestionId, o.SortOrder, o.Value, o.Label))
                .ToList());

        return qRows
            .Select(q => new IntakeQuestion(
                q.Id, q.TemplateId, q.SortOrder,
                Enum.Parse<IntakeQuestionType>(q.QuestionType, ignoreCase: false),
                q.Label, q.HelpText, q.IsRequired, q.DefaultValue, q.DefaultToken,
                optsByQuestion.TryGetValue(q.Id, out var opts) ? opts : Array.Empty<IntakeQuestionOption>()))
            .ToList();
    }

    private static IntakeFormInstance MapInstance(InstanceRow r) => new(
        r.Id, r.TemplateId, r.TicketId, r.SentEventId, r.SubmittedEventId,
        ParseStatus(r.Status), r.ExpiresUtc, r.CreatedUtc, r.SentUtc, r.SubmittedUtc,
        r.SubmitterIp, r.SubmitterUa, r.CreatedBy, r.SentToEmail,
        JsonDocument.Parse(r.PrefillJson));

    private static IntakeFormStatus ParseStatus(string raw) =>
        Enum.Parse<IntakeFormStatus>(raw, ignoreCase: false);

    // --- template snapshot (de)serialisation ---
    // JSON shape matches the SQL backfill in DatabaseBootstrapper — keep
    // the two in sync. CamelCase so the on-disk JSON reads the same as the
    // wire format the frontend already knows from MapAgentViewDto.
    private static readonly JsonSerializerOptions s_snapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string SerializeTemplateSnapshot(IntakeTemplate template)
    {
        var dto = new TemplateSnapshotDto
        {
            Name = template.Name,
            Description = template.Description,
            Questions = template.Questions.Select(q => new QuestionSnapshotDto
            {
                Id = q.Id,
                SortOrder = q.SortOrder,
                Type = q.Type.ToString(),
                Label = q.Label,
                HelpText = q.HelpText,
                IsRequired = q.IsRequired,
                DefaultValue = q.DefaultValue,
                DefaultToken = q.DefaultToken,
                Options = q.Options.Select(o => new OptionSnapshotDto
                {
                    Id = o.Id,
                    SortOrder = o.SortOrder,
                    Value = o.Value,
                    Label = o.Label,
                }).ToList(),
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, s_snapshotJsonOptions);
    }

    private static IntakeTemplate DeserializeTemplateSnapshot(Guid templateId, string json)
    {
        var dto = JsonSerializer.Deserialize<TemplateSnapshotDto>(json, s_snapshotJsonOptions)
                  ?? new TemplateSnapshotDto();
        var questions = (dto.Questions ?? new()).Select(q =>
        {
            var options = (q.Options ?? new())
                .Select(o => new IntakeQuestionOption(o.Id, q.Id, o.SortOrder, o.Value, o.Label))
                .ToList();
            return new IntakeQuestion(
                q.Id, templateId, q.SortOrder,
                Enum.Parse<IntakeQuestionType>(q.Type, ignoreCase: false),
                q.Label, q.HelpText, q.IsRequired, q.DefaultValue, q.DefaultToken,
                options);
        }).ToList();
        // Timestamps + is-active + created-by don't apply to a frozen
        // snapshot — the agent DTO only surfaces template.name/description/
        // questions anyway. Use neutral defaults.
        return new IntakeTemplate(
            templateId,
            dto.Name ?? string.Empty,
            dto.Description,
            IsActive: true,
            CreatedUtc: DateTime.MinValue,
            UpdatedUtc: DateTime.MinValue,
            CreatedBy: null,
            questions);
    }

    private sealed class TemplateSnapshotDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<QuestionSnapshotDto>? Questions { get; set; }
    }

    private sealed class QuestionSnapshotDto
    {
        public long Id { get; set; }
        public int SortOrder { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? HelpText { get; set; }
        public bool IsRequired { get; set; }
        public string? DefaultValue { get; set; }
        public string? DefaultToken { get; set; }
        public List<OptionSnapshotDto>? Options { get; set; }
    }

    private sealed class OptionSnapshotDto
    {
        public long Id { get; set; }
        public int SortOrder { get; set; }
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    // --- row DTOs ---
    // Plain classes with setters. Dapper's ctor-based mapping stumbles on
    // nullable value-type rows (Nullable<record struct> from
    // QueryFirstOrDefaultAsync<TRow?>) and silently returns null even when
    // the row is present — which led to a hard-to-find NRE on Create →
    // GetAsync. Mutable classes map cleanly via column name.

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public Guid TemplateId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? ExpiresUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? SentUtc { get; set; }
        public DateTime? SubmittedUtc { get; set; }
        public string? SentToEmail { get; set; }
    }

    private sealed class InstanceRow
    {
        public Guid Id { get; set; }
        public Guid TemplateId { get; set; }
        public Guid TicketId { get; set; }
        public long? SentEventId { get; set; }
        public long? SubmittedEventId { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? ExpiresUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? SentUtc { get; set; }
        public DateTime? SubmittedUtc { get; set; }
        public string? SubmitterIp { get; set; }
        public string? SubmitterUa { get; set; }
        public Guid? CreatedBy { get; set; }
        public string? SentToEmail { get; set; }
        public string PrefillJson { get; set; } = "{}";
        public string? TemplateSnapshotJson { get; set; }
    }

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

    private sealed class PublicRow
    {
        public Guid InstanceId { get; set; }
        public Guid TemplateId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string? TemplateDescription { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? ExpiresUtc { get; set; }
        public string PrefillJson { get; set; } = "{}";
        public string? TemplateSnapshotJson { get; set; }
    }

    private sealed class SubmitLookupRow
    {
        public Guid InstanceId { get; set; }
        public Guid TicketId { get; set; }
        public Guid TemplateId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public DateTime? ExpiresUtc { get; set; }
        public Guid? SentByUserId { get; set; }
    }

    private sealed record ExpireLookupRow(Guid InstanceId, Guid TicketId, Guid TemplateId, string TemplateName);

    private sealed class AnswerRow
    {
        public string QuestionId { get; set; } = string.Empty;
        public string AnswerJson { get; set; } = string.Empty;
    }
}
