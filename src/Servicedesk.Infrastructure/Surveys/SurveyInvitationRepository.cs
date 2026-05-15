using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Surveys;

namespace Servicedesk.Infrastructure.Surveys;

public sealed class SurveyInvitationRepository : ISurveyInvitationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SurveyInvitationRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<SurveyInvitationCreated?> CreateSentAsync(
        Guid surveyId,
        Guid ticketId,
        byte[] tokenHash,
        byte[] tokenCipher,
        string sentToEmail,
        DateTime expiresUtc,
        IReadOnlyList<Guid> attributedAgentIds,
        string surveySnapshotJson,
        Guid? createdBy,
        string sentEventMetadataJson,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Belt: an active-pair check in the same transaction makes the
        // idempotency-skip path obvious in logs. The partial unique index
        // is the suspenders — if two trigger runners race past this check
        // we still rely on the constraint to keep us honest.
        var active = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM survey_invitations
                WHERE survey_id = @surveyId AND ticket_id = @ticketId AND status = 'Sent'
            )
            """, new { surveyId, ticketId }, transaction: tx, cancellationToken: ct));
        if (active)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        long sentEventId;
        Guid invitationId;
        try
        {
            sentEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
                VALUES (@ticketId, 'SurveySent', @createdBy, @metadataJson::jsonb, TRUE)
                RETURNING id
                """, new { ticketId, createdBy, metadataJson = sentEventMetadataJson },
                transaction: tx, cancellationToken: ct));

            invitationId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO survey_invitations (
                    survey_id, ticket_id, sent_event_id, token_hash, token_cipher,
                    status, sent_to_email, expires_utc, attributed_agent_ids,
                    survey_snapshot_json, created_by)
                VALUES (
                    @surveyId, @ticketId, @sentEventId, @tokenHash, @tokenCipher,
                    'Sent', @sentToEmail, @expiresUtc, @attributedAgentIds,
                    @surveySnapshotJson::jsonb, @createdBy)
                RETURNING id
                """, new
                {
                    surveyId, ticketId, sentEventId, tokenHash, tokenCipher, sentToEmail,
                    expiresUtc,
                    attributedAgentIds = attributedAgentIds.ToArray(),
                    surveySnapshotJson,
                    createdBy,
                }, transaction: tx, cancellationToken: ct));
        }
        catch (PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_survey_invitations_active_pair")
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await tx.CommitAsync(ct);
        return new SurveyInvitationCreated(invitationId, sentEventId);
    }

    public async Task<IReadOnlyList<SurveyInvitationSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        const string sql = $$"""
            {{InvitationSummarySelect}}
            WHERE i.ticket_id = @ticketId
            ORDER BY i.sent_utc DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
        return rows.Select(MapSummary).ToList();
    }

    public async Task<IReadOnlyList<SurveyInvitationSummary>> ListForSurveyAsync(
        Guid surveyId,
        SurveyInvitationStatus? statusFilter,
        int limit,
        CancellationToken ct)
    {
        var sql = $$"""
            {{InvitationSummarySelect}}
            WHERE i.survey_id = @surveyId
            """;
        if (statusFilter is not null) sql += " AND i.status = @status";
        sql += " ORDER BY i.sent_utc DESC LIMIT @limit";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(sql, new
        {
            surveyId,
            status = statusFilter?.ToString(),
            limit = Math.Clamp(limit, 1, 500),
        }, cancellationToken: ct));
        return rows.Select(MapSummary).ToList();
    }

    // Shared SELECT projection so the per-ticket + per-survey list flows
    // surface identical fields including the requester contact + primary
    // company (joined via contact_companies, role='primary' — same pattern
    // ContactSearchSource uses). LEFT JOINs so a deleted contact or a
    // contact without a primary-role company link still renders.
    private const string InvitationSummarySelect = """
        SELECT i.id                                    AS Id,
               i.survey_id                             AS SurveyId,
               s.name                                  AS SurveyName,
               i.ticket_id                             AS TicketId,
               t.number                                AS TicketNumber,
               t.subject                               AS TicketSubject,
               i.status                                AS Status,
               i.sent_to_email                         AS SentToEmail,
               i.sent_utc                              AS SentUtc,
               i.expires_utc                           AS ExpiresUtc,
               i.submitted_utc                         AS SubmittedUtc,
               NULLIF(TRIM(BOTH ' ' FROM
                       COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')),
                       '')                             AS ContactName,
               co.name                                 AS CompanyName
        FROM survey_invitations i
        JOIN surveys s ON s.id = i.survey_id
        JOIN tickets t ON t.id = i.ticket_id
        LEFT JOIN contacts c ON c.id = t.requester_contact_id
        LEFT JOIN contact_companies cc ON cc.contact_id = c.id AND cc.role = 'primary'
        LEFT JOIN companies co ON co.id = cc.company_id
        """;

    public async Task<bool> ActiveExistsAsync(Guid surveyId, Guid ticketId, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM survey_invitations
                WHERE survey_id = @surveyId AND ticket_id = @ticketId AND status = 'Sent'
            )
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { surveyId, ticketId }, cancellationToken: ct));
    }

    public async Task<int> CountForSurveyAsync(Guid surveyId, SurveyInvitationStatus? statusFilter, CancellationToken ct)
    {
        var sql = "SELECT count(*)::int FROM survey_invitations WHERE survey_id = @surveyId";
        if (statusFilter is not null) sql += " AND status = @status";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            surveyId,
            status = statusFilter?.ToString(),
        }, cancellationToken: ct));
    }

    public async Task<SurveyPublicView?> GetByTokenHashForPublicAsync(byte[] tokenHash, CancellationToken ct)
    {
        // Pull every admin-supplied label off the live survey row. The
        // questions still come from the invitation snapshot (frozen at
        // send-time) so a designer edit between mint + submit can't
        // reshape the customer's form. Labels are a deliberate exception:
        // typo fixes on the thank-you / expired messages should reach
        // pending links without the admin having to resend.
        const string sql = """
            SELECT i.id                                AS InvitationId,
                   i.survey_id                         AS SurveyId,
                   s.name                              AS SurveyName,
                   s.intro_html                        AS IntroHtml,
                   s.agent_block_heading               AS AgentBlockHeading,
                   s.submit_button_label               AS SubmitButtonLabel,
                   s.thank_you_message                 AS ThankYouMessage,
                   s.expired_message                   AS ExpiredMessage,
                   s.not_found_message                 AS NotFoundMessage,
                   i.status                            AS Status,
                   i.expires_utc                       AS ExpiresUtc,
                   i.attributed_agent_ids              AS AttributedAgentIds,
                   i.survey_snapshot_json::text        AS SnapshotJson
            FROM survey_invitations i
            JOIN surveys s ON s.id = i.survey_id
            WHERE i.token_hash = @tokenHash AND i.status IN ('Sent','Submitted','Expired')
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<PublicRow>(
            new CommandDefinition(sql, new { tokenHash }, cancellationToken: ct));
        if (row is null) return null;

        var ids = row.AttributedAgentIds ?? Array.Empty<Guid>();
        var agents = await ResolveAgentDisplayNamesAsync(conn, ids, ct);

        // Question shape comes from the invitation's snapshot, not the live
        // survey, so an admin edit between send and submit never reshapes
        // what the customer is looking at.
        var allQuestions = DeserializeQuestionsFromSnapshot(row.SnapshotJson, row.SurveyId);
        var surveyQs = allQuestions.Where(q => q.AppliesTo == SurveyQuestionScope.Survey).OrderBy(q => q.SortOrder).ToList();
        var agentQs = allQuestions.Where(q => q.AppliesTo == SurveyQuestionScope.Agent).OrderBy(q => q.SortOrder).ToList();

        return new SurveyPublicView(
            row.InvitationId, row.SurveyId, row.SurveyName,
            row.IntroHtml ?? string.Empty,
            row.AgentBlockHeading,
            row.SubmitButtonLabel ?? string.Empty,
            row.ThankYouMessage ?? string.Empty,
            row.ExpiredMessage ?? string.Empty,
            row.NotFoundMessage ?? string.Empty,
            ParseStatus(row.Status),
            row.ExpiresUtc,
            agents,
            surveyQs,
            agentQs);
    }

    public async Task<SurveySubmitResult?> TrySubmitAsync(
        byte[] tokenHash,
        SurveySubmitInput input,
        string? ip,
        string? userAgent,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Atomic gate: row-lock the invitation; only Sent + not-expired
        // proceed. Concurrent submits race here and the loser sees null.
        var lookup = await conn.QueryFirstOrDefaultAsync<SubmitLookupRow>(new CommandDefinition("""
            SELECT i.id                     AS InvitationId,
                   i.ticket_id              AS TicketId,
                   t.number                 AS TicketNumber,
                   t.subject                AS TicketSubject,
                   i.survey_id              AS SurveyId,
                   s.name                   AS SurveyName,
                   i.expires_utc            AS ExpiresUtc,
                   i.attributed_agent_ids   AS AttributedAgentIds,
                   t.requester_contact_id   AS RequesterContactId
            FROM survey_invitations i
            JOIN surveys s ON s.id = i.survey_id
            JOIN tickets t ON t.id = i.ticket_id
            WHERE i.token_hash = @tokenHash AND i.status = 'Sent'
            FOR UPDATE OF i
            """, new { tokenHash }, transaction: tx, cancellationToken: ct));

        if (lookup is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        if (lookup.ExpiresUtc <= nowUtc)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var metadataJson = JsonSerializer.Serialize(new
        {
            invitationId = lookup.InvitationId,
            surveyId = lookup.SurveyId,
            surveyName = lookup.SurveyName,
            answerCount = input.Answers.Count,
            agentAnswerCount = input.AgentAnswers.Count,
        });

        // SurveySubmitted is an internal event by default — the customer's
        // mailbox doesn't see helpdesk-side timeline noise. is_internal=TRUE
        // also keeps reopen-on-customer-reply rules that look at non-internal
        // events from accidentally re-triggering for surveys.
        var submittedEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id, body_text, body_html, metadata, is_internal)
            VALUES (@ticketId, 'SurveySubmitted', @authorContactId, NULL, NULL, @metadataJson::jsonb, TRUE)
            RETURNING id
            """,
            new { ticketId = lookup.TicketId, authorContactId = lookup.RequesterContactId, metadataJson },
            transaction: tx, cancellationToken: ct));

        var responseId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO survey_responses (invitation_id, submitted_utc, comment)
            VALUES (@invitationId, @nowUtc, @comment)
            RETURNING id
            """,
            new
            {
                invitationId = lookup.InvitationId,
                nowUtc,
                comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment!.Trim(),
            },
            transaction: tx, cancellationToken: ct));

        // Survey-scope answers: agent_user_id stays NULL so the partial
        // unique index `ux_survey_answers_survey_scope` enforces one row
        // per (response, question).
        foreach (var a in input.Answers)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO survey_answers (response_id, question_id, agent_user_id, value_numeric, value_text, value_json)
                VALUES (@responseId, @questionId, NULL, @valueNumeric, @valueText, CASE WHEN @valueJson IS NULL THEN NULL ELSE @valueJson::jsonb END)
                """,
                new
                {
                    responseId,
                    questionId = a.QuestionId,
                    valueNumeric = a.ValueNumeric,
                    valueText = a.ValueText,
                    valueJson = a.ValueJson,
                },
                transaction: tx, cancellationToken: ct));
        }

        // Agent-scope answers: one row per (agent, question) pair the
        // customer filled in. Unique constraint
        // `ux_survey_answers_agent_scope` keeps duplicates out under
        // concurrent submits (impossible here, but the index is also
        // cheap insurance against caller bugs).
        foreach (var aa in input.AgentAnswers)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO survey_answers (response_id, question_id, agent_user_id, value_numeric, value_text, value_json)
                VALUES (@responseId, @questionId, @agentUserId, @valueNumeric, @valueText, CASE WHEN @valueJson IS NULL THEN NULL ELSE @valueJson::jsonb END)
                """,
                new
                {
                    responseId,
                    questionId = aa.QuestionId,
                    agentUserId = aa.AgentUserId,
                    valueNumeric = aa.ValueNumeric,
                    valueText = aa.ValueText,
                    valueJson = aa.ValueJson,
                },
                transaction: tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE survey_invitations
            SET status = 'Submitted',
                submitted_utc = @nowUtc,
                submitted_event_id = @submittedEventId,
                submitter_ip = CASE WHEN @ip IS NULL THEN NULL ELSE @ip::inet END,
                submitter_ua = @userAgent
            WHERE id = @invitationId AND status = 'Sent'
            """,
            new
            {
                invitationId = lookup.InvitationId,
                nowUtc,
                submittedEventId,
                ip,
                userAgent,
            },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        // Notify every attributed agent. We used to filter by "agent that
        // received a per-agent score"; with the new sub-question model,
        // every attributed agent has the same set of questions in front of
        // the customer, so they all care about the submission.
        var notify = (lookup.AttributedAgentIds ?? Array.Empty<Guid>()).Distinct().ToList();

        return new SurveySubmitResult(
            lookup.InvitationId, lookup.TicketId, lookup.TicketNumber, lookup.TicketSubject,
            lookup.SurveyId, lookup.SurveyName,
            submittedEventId, notify);
    }

    public async Task<IReadOnlyList<SurveyExpiredInstance>> ExpireStaleAsync(int maxBatch, DateTime nowUtc, CancellationToken ct)
    {
        if (maxBatch <= 0) return Array.Empty<SurveyExpiredInstance>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var stale = (await conn.QueryAsync<ExpireLookupRow>(new CommandDefinition("""
            SELECT i.id AS InvitationId, i.ticket_id AS TicketId, i.survey_id AS SurveyId, s.name AS SurveyName
            FROM survey_invitations i
            JOIN surveys s ON s.id = i.survey_id
            WHERE i.status = 'Sent' AND i.expires_utc <= @nowUtc
            ORDER BY i.expires_utc
            LIMIT @maxBatch
            FOR UPDATE OF i SKIP LOCKED
            """, new { nowUtc, maxBatch }, transaction: tx, cancellationToken: ct))).ToList();

        if (stale.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return Array.Empty<SurveyExpiredInstance>();
        }

        var result = new List<SurveyExpiredInstance>(stale.Count);
        foreach (var s in stale)
        {
            var metadataJson = JsonSerializer.Serialize(new
            {
                invitationId = s.InvitationId,
                surveyId = s.SurveyId,
                surveyName = s.SurveyName,
            });

            var expiredEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO ticket_events (ticket_id, event_type, metadata, is_internal)
                VALUES (@ticketId, 'SurveyExpired', @metadataJson::jsonb, TRUE)
                RETURNING id
                """, new { ticketId = s.TicketId, metadataJson },
                transaction: tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE survey_invitations
                SET status = 'Expired'
                WHERE id = @invitationId AND status = 'Sent'
                """, new { invitationId = s.InvitationId },
                transaction: tx, cancellationToken: ct));

            result.Add(new SurveyExpiredInstance(s.InvitationId, s.TicketId, s.SurveyId, s.SurveyName, expiredEventId));
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<bool> CancelSentAsync(Guid invitationId, CancellationToken ct)
    {
        const string sql = """
            UPDATE survey_invitations
            SET status = 'Cancelled', cancelled_utc = now()
            WHERE id = @invitationId AND status = 'Sent'
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { invitationId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<SurveyAggregateResults> GetAggregateResultsAsync(Guid surveyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var counts = await conn.QueryFirstOrDefaultAsync<CountsRow>(new CommandDefinition("""
            SELECT
                COALESCE(SUM(CASE WHEN status = 'Sent'      THEN 1 ELSE 0 END), 0)::int AS Sent,
                COALESCE(SUM(CASE WHEN status = 'Submitted' THEN 1 ELSE 0 END), 0)::int AS Submitted,
                COALESCE(SUM(CASE WHEN status = 'Expired'   THEN 1 ELSE 0 END), 0)::int AS Expired,
                COALESCE(SUM(CASE WHEN status = 'Cancelled' THEN 1 ELSE 0 END), 0)::int AS Cancelled,
                COUNT(*)::int AS Total
            FROM survey_invitations
            WHERE survey_id = @surveyId
            """, new { surveyId }, cancellationToken: ct)) ?? new CountsRow();

        // Leaderboard: collapse all numeric Agent-scope answers per agent.
        // Non-numeric (Text/SingleChoice/MultiChoice) answers still count
        // toward ResponseCount via the distinct-response join but don't
        // affect AverageRating.
        var leaderboard = (await conn.QueryAsync<LeaderboardRow>(new CommandDefinition("""
            SELECT a.agent_user_id                              AS AgentUserId,
                   COALESCE(u.email::text, '?')                 AS DisplayName,
                   COUNT(DISTINCT r.id)::int                    AS ResponseCount,
                   AVG(a.value_numeric)::numeric(10,2)          AS AverageRating
            FROM survey_answers a
            JOIN survey_responses r ON r.id = a.response_id
            JOIN survey_invitations i ON i.id = r.invitation_id
            LEFT JOIN users u ON u.id = a.agent_user_id
            WHERE i.survey_id = @surveyId AND a.agent_user_id IS NOT NULL
            GROUP BY a.agent_user_id, u.email
            ORDER BY AVG(a.value_numeric) DESC NULLS LAST, COUNT(DISTINCT r.id) DESC
            """, new { surveyId }, cancellationToken: ct))).ToList();

        var questionAggs = new List<SurveyQuestionAggregate>();
        var agentQuestionAggs = new List<SurveyAgentQuestionAggregate>();
        var qList = await SurveyRepository.LoadQuestionsAsync(conn, surveyId, ct);

        foreach (var q in qList)
        {
            if (q.AppliesTo == SurveyQuestionScope.Survey)
            {
                questionAggs.Add(await AggregateSurveyQuestionAsync(conn, surveyId, q, ct));
            }
            else
            {
                agentQuestionAggs.AddRange(await AggregateAgentQuestionAsync(conn, surveyId, q, ct));
            }
        }

        return new SurveyAggregateResults(
            surveyId,
            counts.Sent + counts.Submitted + counts.Expired + counts.Cancelled,
            counts.Submitted,
            counts.Expired,
            counts.Cancelled,
            leaderboard
                .Select(l => new SurveyAgentLeaderboardRow(l.AgentUserId, l.DisplayName, l.ResponseCount, l.AverageRating))
                .ToList(),
            questionAggs,
            agentQuestionAggs);
    }

    public async Task<SurveyResponseDetail?> GetResponseDetailAsync(Guid invitationId, CancellationToken ct)
    {
        const string headSql = """
            SELECT i.id                          AS InvitationId,
                   i.ticket_id                   AS TicketId,
                   t.number                      AS TicketNumber,
                   t.subject                     AS TicketSubject,
                   i.sent_to_email               AS SentToEmail,
                   i.sent_utc                    AS SentUtc,
                   r.submitted_utc               AS SubmittedUtc,
                   r.comment                     AS Comment,
                   i.survey_snapshot_json::text  AS SurveySnapshotJson,
                   r.id                          AS ResponseId
            FROM survey_invitations i
            JOIN survey_responses r ON r.invitation_id = i.id
            JOIN tickets t ON t.id = i.ticket_id
            WHERE i.id = @invitationId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var head = await conn.QueryFirstOrDefaultAsync<DetailHeadRow>(
            new CommandDefinition(headSql, new { invitationId }, cancellationToken: ct));
        if (head is null) return null;

        var allAnswers = (await conn.QueryAsync<DetailAnswerRow>(new CommandDefinition("""
            SELECT a.question_id          AS QuestionId,
                   a.agent_user_id        AS AgentUserId,
                   COALESCE(u.email::text, '?') AS AgentDisplayName,
                   a.value_numeric        AS ValueNumeric,
                   a.value_text           AS ValueText,
                   a.value_json::text     AS ValueJson
            FROM survey_answers a
            LEFT JOIN users u ON u.id = a.agent_user_id
            WHERE a.response_id = @responseId
            ORDER BY a.id
            """, new { responseId = head.ResponseId }, cancellationToken: ct))).ToList();

        var surveyAnswers = allAnswers
            .Where(a => a.AgentUserId is null)
            .Select(a => new SurveyAnswerView(
                a.QuestionId, a.ValueNumeric, a.ValueText,
                string.IsNullOrEmpty(a.ValueJson) ? null : JsonDocument.Parse(a.ValueJson)))
            .ToList();

        var agentAnswers = allAnswers
            .Where(a => a.AgentUserId is not null)
            .Select(a => new SurveyAgentAnswerView(
                a.AgentUserId!.Value, a.AgentDisplayName,
                a.QuestionId, a.ValueNumeric, a.ValueText,
                string.IsNullOrEmpty(a.ValueJson) ? null : JsonDocument.Parse(a.ValueJson)))
            .ToList();

        return new SurveyResponseDetail(
            head.InvitationId, head.TicketId, head.TicketNumber, head.TicketSubject,
            head.SentToEmail, head.SentUtc, head.SubmittedUtc, head.Comment,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(head.SurveySnapshotJson) ? "{}" : head.SurveySnapshotJson),
            surveyAnswers,
            agentAnswers);
    }

    // --- helpers ---

    private static SurveyInvitationSummary MapSummary(SummaryRow r) => new(
        r.Id, r.SurveyId, r.SurveyName, r.TicketId, r.TicketNumber, r.TicketSubject,
        ParseStatus(r.Status), r.SentToEmail, r.SentUtc, r.ExpiresUtc, r.SubmittedUtc,
        r.ContactName, r.CompanyName);

    private static async Task<SurveyQuestionAggregate> AggregateSurveyQuestionAsync(
        NpgsqlConnection conn, Guid surveyId, SurveyQuestion q, CancellationToken ct)
    {
        switch (q.Type)
        {
            case SurveyQuestionType.Rating:
            case SurveyQuestionType.Nps:
            {
                var stat = await conn.QueryFirstOrDefaultAsync<NumericAggRow>(new CommandDefinition("""
                    SELECT COUNT(a.id)::int           AS AnswerCount,
                           AVG(a.value_numeric)::numeric(10,2) AS AverageNumeric
                    FROM survey_answers a
                    JOIN survey_responses r ON r.id = a.response_id
                    JOIN survey_invitations i ON i.id = r.invitation_id
                    WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NULL
                    """, new { surveyId, qid = q.Id }, cancellationToken: ct)) ?? new NumericAggRow();

                var bucketRows = await conn.QueryAsync<NumericBucketRow>(new CommandDefinition("""
                    SELECT a.value_numeric::text AS Bucket, COUNT(*)::int AS Count
                    FROM survey_answers a
                    JOIN survey_responses r ON r.id = a.response_id
                    JOIN survey_invitations i ON i.id = r.invitation_id
                    WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NULL AND a.value_numeric IS NOT NULL
                    GROUP BY a.value_numeric
                    ORDER BY a.value_numeric
                    """, new { surveyId, qid = q.Id }, cancellationToken: ct));
                var tallyDict = bucketRows.ToDictionary(b => b.Bucket, b => b.Count);
                var tallyJson = JsonDocument.Parse(JsonSerializer.Serialize(tallyDict));
                return new SurveyQuestionAggregate(q.Id, q.Label, q.Type, stat.AnswerCount, stat.AverageNumeric, tallyJson);
            }
            case SurveyQuestionType.SingleChoice:
            case SurveyQuestionType.MultiChoice:
            {
                var tally = new Dictionary<string, int>(StringComparer.Ordinal);
                int answerCount = 0;

                if (q.Type == SurveyQuestionType.SingleChoice)
                {
                    var rows = await conn.QueryAsync<TextBucketRow>(new CommandDefinition("""
                        SELECT a.value_text AS Bucket, COUNT(*)::int AS Count
                        FROM survey_answers a
                        JOIN survey_responses r ON r.id = a.response_id
                        JOIN survey_invitations i ON i.id = r.invitation_id
                        WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NULL AND a.value_text IS NOT NULL
                        GROUP BY a.value_text
                        """, new { surveyId, qid = q.Id }, cancellationToken: ct));
                    foreach (var r in rows)
                    {
                        tally[r.Bucket ?? ""] = r.Count;
                        answerCount += r.Count;
                    }
                }
                else
                {
                    var rows = await conn.QueryAsync<TextBucketRow>(new CommandDefinition("""
                        SELECT v.value AS Bucket, COUNT(*)::int AS Count
                        FROM survey_answers a
                        JOIN survey_responses r ON r.id = a.response_id
                        JOIN survey_invitations i ON i.id = r.invitation_id
                        CROSS JOIN LATERAL jsonb_array_elements_text(a.value_json) AS v(value)
                        WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NULL AND a.value_json IS NOT NULL
                        GROUP BY v.value
                        """, new { surveyId, qid = q.Id }, cancellationToken: ct));
                    foreach (var r in rows)
                    {
                        tally[r.Bucket ?? ""] = r.Count;
                    }

                    answerCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                        SELECT COUNT(*)::int
                        FROM survey_answers a
                        JOIN survey_responses r ON r.id = a.response_id
                        JOIN survey_invitations i ON i.id = r.invitation_id
                        WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NULL AND a.value_json IS NOT NULL
                        """, new { surveyId, qid = q.Id }, cancellationToken: ct));
                }

                var tallyJson = JsonDocument.Parse(JsonSerializer.Serialize(tally));
                return new SurveyQuestionAggregate(q.Id, q.Label, q.Type, answerCount, null, tallyJson);
            }
            default:
            {
                var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*)::int
                    FROM survey_answers a
                    JOIN survey_responses r ON r.id = a.response_id
                    JOIN survey_invitations i ON i.id = r.invitation_id
                    WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NULL
                      AND a.value_text IS NOT NULL AND length(trim(a.value_text)) > 0
                    """, new { surveyId, qid = q.Id }, cancellationToken: ct));
                return new SurveyQuestionAggregate(q.Id, q.Label, q.Type, count, null, null);
            }
        }
    }

    private static async Task<IReadOnlyList<SurveyAgentQuestionAggregate>> AggregateAgentQuestionAsync(
        NpgsqlConnection conn, Guid surveyId, SurveyQuestion q, CancellationToken ct)
    {
        // Per-agent breakdown for a single Agent-scope question. The agent
        // axis lives in `survey_answers.agent_user_id`; we group on that
        // plus the question id to get one aggregate row per (question,
        // agent) pair that ever received an answer.
        var rows = await conn.QueryAsync<AgentQuestionAggRow>(new CommandDefinition("""
            SELECT a.agent_user_id                          AS AgentUserId,
                   COALESCE(u.email::text, '?')             AS AgentDisplayName,
                   COUNT(*)::int                            AS AnswerCount,
                   AVG(a.value_numeric)::numeric(10,2)      AS AverageNumeric
            FROM survey_answers a
            JOIN survey_responses r ON r.id = a.response_id
            JOIN survey_invitations i ON i.id = r.invitation_id
            LEFT JOIN users u ON u.id = a.agent_user_id
            WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id IS NOT NULL
            GROUP BY a.agent_user_id, u.email
            ORDER BY u.email NULLS LAST
            """, new { surveyId, qid = q.Id }, cancellationToken: ct));

        var output = new List<SurveyAgentQuestionAggregate>();
        foreach (var r in rows)
        {
            // For choice types we also tally option values per agent; the
            // detail view uses this to render a stacked bar per agent.
            JsonDocument? tally = null;
            if (q.Type == SurveyQuestionType.SingleChoice)
            {
                var buckets = await conn.QueryAsync<TextBucketRow>(new CommandDefinition("""
                    SELECT a.value_text AS Bucket, COUNT(*)::int AS Count
                    FROM survey_answers a
                    JOIN survey_responses r ON r.id = a.response_id
                    JOIN survey_invitations i ON i.id = r.invitation_id
                    WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id = @agentId AND a.value_text IS NOT NULL
                    GROUP BY a.value_text
                    """, new { surveyId, qid = q.Id, agentId = r.AgentUserId }, cancellationToken: ct));
                tally = JsonDocument.Parse(JsonSerializer.Serialize(buckets.ToDictionary(b => b.Bucket ?? "", b => b.Count)));
            }
            else if (q.Type == SurveyQuestionType.MultiChoice)
            {
                var buckets = await conn.QueryAsync<TextBucketRow>(new CommandDefinition("""
                    SELECT v.value AS Bucket, COUNT(*)::int AS Count
                    FROM survey_answers a
                    JOIN survey_responses r ON r.id = a.response_id
                    JOIN survey_invitations i ON i.id = r.invitation_id
                    CROSS JOIN LATERAL jsonb_array_elements_text(a.value_json) AS v(value)
                    WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id = @agentId AND a.value_json IS NOT NULL
                    GROUP BY v.value
                    """, new { surveyId, qid = q.Id, agentId = r.AgentUserId }, cancellationToken: ct));
                tally = JsonDocument.Parse(JsonSerializer.Serialize(buckets.ToDictionary(b => b.Bucket ?? "", b => b.Count)));
            }
            else if (q.Type == SurveyQuestionType.Rating || q.Type == SurveyQuestionType.Nps)
            {
                var buckets = await conn.QueryAsync<NumericBucketRow>(new CommandDefinition("""
                    SELECT a.value_numeric::text AS Bucket, COUNT(*)::int AS Count
                    FROM survey_answers a
                    JOIN survey_responses r ON r.id = a.response_id
                    JOIN survey_invitations i ON i.id = r.invitation_id
                    WHERE i.survey_id = @surveyId AND a.question_id = @qid AND a.agent_user_id = @agentId AND a.value_numeric IS NOT NULL
                    GROUP BY a.value_numeric
                    ORDER BY a.value_numeric
                    """, new { surveyId, qid = q.Id, agentId = r.AgentUserId }, cancellationToken: ct));
                tally = JsonDocument.Parse(JsonSerializer.Serialize(buckets.ToDictionary(b => b.Bucket, b => b.Count)));
            }

            output.Add(new SurveyAgentQuestionAggregate(
                q.Id, q.Label, q.Type, r.AgentUserId, r.AgentDisplayName,
                r.AnswerCount, r.AverageNumeric, tally));
        }
        return output;
    }

    private static async Task<IReadOnlyList<SurveyAttributedAgent>> ResolveAgentDisplayNamesAsync(
        NpgsqlConnection conn, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<SurveyAttributedAgent>();
        const string sql = """
            SELECT id                       AS UserId,
                   COALESCE(email::text, '?') AS DisplayName
            FROM users
            WHERE id = ANY(@ids)
            """;
        var rows = await conn.QueryAsync<AgentRow>(new CommandDefinition(sql, new { ids = ids.ToArray() }, cancellationToken: ct));
        var byId = rows.ToDictionary(r => r.UserId, r => r.DisplayName);
        // Preserve send-time order so the public page renders rating-blocks
        // consistently across refreshes.
        return ids.Select(id => new SurveyAttributedAgent(id, byId.GetValueOrDefault(id, "Unknown agent"))).ToList();
    }

    private static IReadOnlyList<SurveyQuestion> DeserializeQuestionsFromSnapshot(string? snapshotJson, Guid surveyId)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return Array.Empty<SurveyQuestion>();
        var dto = JsonSerializer.Deserialize<SnapshotDto>(snapshotJson, s_snapshotJsonOptions);
        if (dto?.Questions is null) return Array.Empty<SurveyQuestion>();
        return dto.Questions.Select(q => new SurveyQuestion(
            q.Id, surveyId, q.SortOrder,
            Enum.Parse<SurveyQuestionType>(q.Type ?? "Text", ignoreCase: false),
            Enum.Parse<SurveyQuestionScope>(string.IsNullOrWhiteSpace(q.AppliesTo) ? "Survey" : q.AppliesTo, ignoreCase: false),
            q.Label ?? string.Empty, q.HelpText, q.IsRequired,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(q.ConfigJson) ? "{}" : q.ConfigJson))).ToList();
    }

    private static readonly JsonSerializerOptions s_snapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static SurveyInvitationStatus ParseStatus(string raw) =>
        Enum.Parse<SurveyInvitationStatus>(raw, ignoreCase: false);

    // --- row DTOs ---

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public Guid SurveyId { get; set; }
        public string SurveyName { get; set; } = string.Empty;
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public string Status { get; set; } = "Sent";
        public string SentToEmail { get; set; } = string.Empty;
        public DateTime SentUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? SubmittedUtc { get; set; }
        public string? ContactName { get; set; }
        public string? CompanyName { get; set; }
    }

    private sealed class PublicRow
    {
        public Guid InvitationId { get; set; }
        public Guid SurveyId { get; set; }
        public string SurveyName { get; set; } = string.Empty;
        public string? IntroHtml { get; set; }
        public string? AgentBlockHeading { get; set; }
        public string? SubmitButtonLabel { get; set; }
        public string? ThankYouMessage { get; set; }
        public string? ExpiredMessage { get; set; }
        public string? NotFoundMessage { get; set; }
        public string Status { get; set; } = "Sent";
        public DateTime ExpiresUtc { get; set; }
        public Guid[]? AttributedAgentIds { get; set; }
        public string? SnapshotJson { get; set; }
    }

    private sealed class SubmitLookupRow
    {
        public Guid InvitationId { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public Guid SurveyId { get; set; }
        public string SurveyName { get; set; } = string.Empty;
        public DateTime ExpiresUtc { get; set; }
        public Guid[]? AttributedAgentIds { get; set; }
        public Guid? RequesterContactId { get; set; }
    }

    private sealed record ExpireLookupRow(Guid InvitationId, Guid TicketId, Guid SurveyId, string SurveyName);

    private sealed class CountsRow
    {
        public int Sent { get; set; }
        public int Submitted { get; set; }
        public int Expired { get; set; }
        public int Cancelled { get; set; }
        public int Total { get; set; }
    }

    private sealed class LeaderboardRow
    {
        public Guid AgentUserId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public int ResponseCount { get; set; }
        public decimal? AverageRating { get; set; }
    }

    private sealed class NumericAggRow
    {
        public int AnswerCount { get; set; }
        public decimal? AverageNumeric { get; set; }
    }

    private sealed class NumericBucketRow
    {
        public string Bucket { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class TextBucketRow
    {
        public string? Bucket { get; set; }
        public int Count { get; set; }
    }

    private sealed class AgentRow
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class AgentQuestionAggRow
    {
        public Guid AgentUserId { get; set; }
        public string AgentDisplayName { get; set; } = string.Empty;
        public int AnswerCount { get; set; }
        public decimal? AverageNumeric { get; set; }
    }

    private sealed class DetailHeadRow
    {
        public Guid InvitationId { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public string SentToEmail { get; set; } = string.Empty;
        public DateTime SentUtc { get; set; }
        public DateTime SubmittedUtc { get; set; }
        public string? Comment { get; set; }
        public string? SurveySnapshotJson { get; set; }
        public Guid ResponseId { get; set; }
    }

    private sealed class DetailAnswerRow
    {
        public long QuestionId { get; set; }
        public Guid? AgentUserId { get; set; }
        public string AgentDisplayName { get; set; } = string.Empty;
        public decimal? ValueNumeric { get; set; }
        public string? ValueText { get; set; }
        public string? ValueJson { get; set; }
    }

    private sealed class SnapshotDto
    {
        public string? Name { get; set; }
        public string? IntroHtml { get; set; }
        public List<SnapshotQuestionDto>? Questions { get; set; }
    }

    private sealed class SnapshotQuestionDto
    {
        public long Id { get; set; }
        public int SortOrder { get; set; }
        public string? Type { get; set; }
        public string? AppliesTo { get; set; }
        public string? Label { get; set; }
        public string? HelpText { get; set; }
        public bool IsRequired { get; set; }
        public string? ConfigJson { get; set; }
    }
}
