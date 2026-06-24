using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Feedback;

public sealed class FeedbackEntryService : IFeedbackEntryService
{
    private const string SelectFromJoin = """
        SELECT  e.id                       AS Id,
                e.target_user_id            AS TargetUserId,
                tu.email                    AS TargetUserEmail,
                e.entry_date                AS EntryDate,
                e.body_html                 AS BodyHtml,
                e.management_remarks_html   AS ManagementRemarksHtml,
                e.work_point_type_id        AS WorkPointTypeId,
                wt.name                     AS WorkPointTypeName,
                wt.color                    AS WorkPointTypeColor,
                e.is_completed              AS IsCompleted,
                e.completed_by_user_id      AS CompletedByUserId,
                cu.email                    AS CompletedByEmail,
                e.completed_utc             AS CompletedUtc,
                e.mgmt_reviewed             AS MgmtReviewed,
                e.mgmt_reviewed_by_user_id  AS MgmtReviewedByUserId,
                mr.email                    AS MgmtReviewedByEmail,
                e.mgmt_reviewed_utc         AS MgmtReviewedUtc,
                e.linked_ticket_id          AS LinkedTicketId,
                e.linked_ticket_number      AS LinkedTicketNumber,
                k.subject                   AS LinkedTicketSubject,
                e.linked_ticket_event_id    AS LinkedTicketEventId,
                e.source                    AS Source,
                e.created_by_user_id        AS CreatedByUserId,
                cb.email                    AS CreatedByEmail,
                e.created_utc               AS CreatedUtc,
                e.updated_utc               AS UpdatedUtc
        FROM feedback_entries e
        JOIN users tu                       ON tu.id = e.target_user_id
        LEFT JOIN feedback_work_point_types wt ON wt.id = e.work_point_type_id
        LEFT JOIN users cu                  ON cu.id = e.completed_by_user_id
        LEFT JOIN users mr                  ON mr.id = e.mgmt_reviewed_by_user_id
        LEFT JOIN users cb                  ON cb.id = e.created_by_user_id
        LEFT JOIN tickets k                 ON k.id = e.linked_ticket_id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITicketNumberLookup _ticketLookup;
    private readonly IKbHtmlSanitizer _sanitizer;
    private readonly ISettingsService _settings;

    public FeedbackEntryService(
        NpgsqlDataSource dataSource,
        ITicketNumberLookup ticketLookup,
        IKbHtmlSanitizer sanitizer,
        ISettingsService settings)
    {
        _dataSource = dataSource;
        _ticketLookup = ticketLookup;
        _sanitizer = sanitizer;
        _settings = settings;
    }

    public async Task<IReadOnlyList<FeedbackEntryRow>> ListAsync(
        FeedbackEntryFilter filter, Guid actorUserId, bool ownOnly, CancellationToken ct = default)
    {
        var sql = SelectFromJoin + """

            WHERE (@actor           IS NULL OR e.created_by_user_id  = @actor)
              AND (@targetUserId    IS NULL OR e.target_user_id    = @targetUserId)
              AND (@workPointTypeId IS NULL OR e.work_point_type_id = @workPointTypeId)
              AND (@isCompleted     IS NULL OR e.is_completed        = @isCompleted)
            ORDER BY e.entry_date DESC, e.created_utc DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FeedbackEntryRow>(new CommandDefinition(
            sql,
            new
            {
                actor = ownOnly ? actorUserId : (Guid?)null,
                targetUserId = filter.TargetUserId,
                workPointTypeId = filter.WorkPointTypeId,
                isCompleted = filter.IsCompleted,
            },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<FeedbackEmployee>> ListEmployeesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS Id, email AS Email
            FROM users
            WHERE is_active = TRUE AND role_name IN ('Agent','Admin')
            ORDER BY email
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FeedbackEmployee>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<FeedbackLoggedEvent>> ListLoggedEventsAsync(
        Guid ticketId, Guid actorUserId, bool ownOnly, CancellationToken ct = default)
    {
        const string sql = """
            SELECT  e.linked_ticket_event_id                      AS EventId,
                    count(*)::int                                  AS Count,
                    array_remove(array_agg(DISTINCT cb.email), NULL) AS Loggers
            FROM feedback_entries e
            LEFT JOIN users cb ON cb.id = e.created_by_user_id
            WHERE e.linked_ticket_id = @ticketId
              AND e.linked_ticket_event_id IS NOT NULL
              AND (@actor IS NULL OR e.created_by_user_id = @actor)
            GROUP BY e.linked_ticket_event_id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FeedbackLoggedEvent>(
            new CommandDefinition(
                sql,
                new { ticketId, actor = ownOnly ? actorUserId : (Guid?)null },
                cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<FeedbackEntryRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var sql = SelectFromJoin + "\n\nWHERE e.id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FeedbackEntryRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<CreateFeedbackEntryResult> CreateAsync(
        Guid actorUserId, Guid? targetUserId, CancellationToken ct = default)
    {
        var target = targetUserId ?? actorUserId;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        if (!await IsActiveStaffAsync(conn, target, ct))
            return new CreateFeedbackEntryResult.ValidationFailed(
                new[] { new FeedbackFieldError("targetUserId", "Select an active employee.") });

        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO feedback_entries (target_user_id, created_by_user_id)
            VALUES (@target, @actor)
            RETURNING id
            """,
            new { target, actor = actorUserId },
            cancellationToken: ct));

        var saved = await GetAsync(id, ct);
        return new CreateFeedbackEntryResult.Created(saved!);
    }

    public async Task<CreateFeedbackEntryResult> LogAsync(
        Guid actorUserId, LogFeedbackInput input, CancellationToken ct = default)
    {
        var bodyHtml = _sanitizer.Sanitize(input.BodyHtml);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var errors = new List<FeedbackFieldError>();
        if (!await IsActiveStaffAsync(conn, input.TargetUserId, ct))
            errors.Add(new FeedbackFieldError("targetUserId", "Select an active employee."));

        var maxChars = await _settings.GetAsync<int>(SettingKeys.Feedback.BodyMaxChars, ct);
        if (maxChars <= 0) maxChars = 20000;
        if (bodyHtml.Length > maxChars)
            errors.Add(new FeedbackFieldError("bodyHtml", $"Feedback exceeds the {maxChars}-character limit."));

        if (input.WorkPointTypeId is { } typeId && typeId != Guid.Empty)
        {
            var typeOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM feedback_work_point_types WHERE id = @typeId AND is_active = TRUE)",
                new { typeId }, cancellationToken: ct));
            if (!typeOk) errors.Add(new FeedbackFieldError("workPointTypeId", "Selected work-point type no longer exists."));
        }

        if (errors.Count > 0) return new CreateFeedbackEntryResult.ValidationFailed(errors);

        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO feedback_entries (
                target_user_id, entry_date, body_html, work_point_type_id,
                linked_ticket_id, linked_ticket_number, linked_ticket_event_id,
                source, created_by_user_id)
            VALUES (
                @target, @entryDate, @bodyHtml, @workPointTypeId,
                @ticketId, @ticketNumber, @ticketEventId,
                'activity', @actor)
            RETURNING id
            """,
            new
            {
                target = input.TargetUserId,
                entryDate = input.EntryDate.ToDateTime(TimeOnly.MinValue),
                bodyHtml,
                workPointTypeId = input.WorkPointTypeId == Guid.Empty ? (Guid?)null : input.WorkPointTypeId,
                ticketId = input.LinkedTicketId,
                ticketNumber = input.LinkedTicketNumber,
                ticketEventId = input.LinkedTicketEventId,
                actor = actorUserId,
            },
            cancellationToken: ct));

        var saved = await GetAsync(id, ct);
        return new CreateFeedbackEntryResult.Created(saved!);
    }

    public async Task<UpdateFeedbackEntryResult> UpdateAsync(
        Guid id, Guid actorUserId, FeedbackEntryInput input, bool ownOnly, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct);
        if (existing is null) return new UpdateFeedbackEntryResult.NotFound();

        // Restricted users may only touch their own rows; report NotFound for
        // anything else so the row's existence is not revealed.
        if (ownOnly && existing.CreatedByUserId != actorUserId)
            return new UpdateFeedbackEntryResult.NotFound();

        var bodyHtml = _sanitizer.Sanitize(input.BodyHtml);
        // Management fields are read-only for restricted users — preserve the
        // stored values regardless of what the client sent.
        var remarksHtml = ownOnly
            ? existing.ManagementRemarksHtml
            : _sanitizer.Sanitize(input.ManagementRemarksHtml);
        var isCompleted = ownOnly ? existing.IsCompleted : input.IsCompleted;
        var isMgmtReviewed = ownOnly ? existing.MgmtReviewed : input.IsMgmtReviewed;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var errors = new List<FeedbackFieldError>();

        if (!await IsActiveStaffAsync(conn, input.TargetUserId, ct))
            errors.Add(new FeedbackFieldError("targetUserId", "Select an active employee."));

        var maxChars = await _settings.GetAsync<int>(SettingKeys.Feedback.BodyMaxChars, ct);
        if (maxChars <= 0) maxChars = 20000;
        if (bodyHtml.Length > maxChars)
            errors.Add(new FeedbackFieldError("bodyHtml", $"Feedback exceeds the {maxChars}-character limit."));
        // Only validate remarks length when the caller can actually change them.
        if (!ownOnly && remarksHtml.Length > maxChars)
            errors.Add(new FeedbackFieldError("managementRemarksHtml", $"Management remarks exceed the {maxChars}-character limit."));

        if (input.WorkPointTypeId is { } typeId && typeId != Guid.Empty)
        {
            var typeOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM feedback_work_point_types WHERE id = @typeId AND is_active = TRUE)",
                new { typeId }, cancellationToken: ct));
            if (!typeOk) errors.Add(new FeedbackFieldError("workPointTypeId", "Selected work-point type no longer exists."));
        }

        // Resolve the typed ticket number to a live ticket id. Not found is NOT
        // a validation error — the number is stored without a clickable link.
        Guid? ticketId = null;
        if (input.LinkedTicketNumber is { } number && number > 0)
            ticketId = await _ticketLookup.GetIdByNumberAsync(number, ct);

        if (errors.Count > 0) return new UpdateFeedbackEntryResult.ValidationFailed(errors);

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE feedback_entries SET
                target_user_id          = @target,
                entry_date              = @entryDate,
                body_html               = @bodyHtml,
                management_remarks_html = @remarksHtml,
                work_point_type_id      = @workPointTypeId,
                is_completed            = @isCompleted,
                completed_by_user_id    = CASE WHEN @isCompleted
                                               THEN COALESCE(completed_by_user_id, @actor) END,
                completed_utc           = CASE WHEN @isCompleted
                                               THEN COALESCE(completed_utc, now()) END,
                mgmt_reviewed           = @isMgmtReviewed,
                mgmt_reviewed_by_user_id = CASE WHEN @isMgmtReviewed
                                               THEN COALESCE(mgmt_reviewed_by_user_id, @actor) END,
                mgmt_reviewed_utc       = CASE WHEN @isMgmtReviewed
                                               THEN COALESCE(mgmt_reviewed_utc, now()) END,
                linked_ticket_id        = @ticketId,
                linked_ticket_number    = @ticketNumber,
                -- Drop the timeline deep-link if the ticket is changed away
                -- from the one the logged event belongs to; keep it otherwise.
                linked_ticket_event_id  = CASE WHEN @ticketId IS DISTINCT FROM linked_ticket_id
                                               THEN NULL ELSE linked_ticket_event_id END,
                updated_utc             = now()
            WHERE id = @id
            """,
            new
            {
                id,
                actor = actorUserId,
                target = input.TargetUserId,
                entryDate = input.EntryDate.ToDateTime(TimeOnly.MinValue),
                bodyHtml,
                remarksHtml,
                workPointTypeId = input.WorkPointTypeId == Guid.Empty ? (Guid?)null : input.WorkPointTypeId,
                isCompleted,
                isMgmtReviewed,
                ticketId,
                ticketNumber = input.LinkedTicketNumber,
            },
            cancellationToken: ct));
        if (rows == 0) return new UpdateFeedbackEntryResult.NotFound();

        var saved = await GetAsync(id, ct);
        return new UpdateFeedbackEntryResult.Updated(saved!);
    }

    public async Task<FeedbackEntryRow?> SetCompletedAsync(
        Guid id, Guid actorUserId, bool completed, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE feedback_entries SET
                is_completed         = @completed,
                completed_by_user_id = CASE WHEN @completed THEN @actor END,
                completed_utc        = CASE WHEN @completed THEN now() END,
                updated_utc          = now()
            WHERE id = @id
            """,
            new { id, actor = actorUserId, completed },
            cancellationToken: ct));
        if (rows == 0) return null;
        return await GetAsync(id, ct);
    }

    public async Task<FeedbackEntryRow?> SetMgmtReviewedAsync(
        Guid id, Guid actorUserId, bool reviewed, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE feedback_entries SET
                mgmt_reviewed            = @reviewed,
                mgmt_reviewed_by_user_id = CASE WHEN @reviewed THEN @actor END,
                mgmt_reviewed_utc        = CASE WHEN @reviewed THEN now() END,
                updated_utc              = now()
            WHERE id = @id
            """,
            new { id, actor = actorUserId, reviewed },
            cancellationToken: ct));
        if (rows == 0) return null;
        return await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid actorUserId, bool ownOnly, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM feedback_entries
            WHERE id = @id
              AND (@actor IS NULL OR created_by_user_id = @actor)
            """,
            new { id, actor = ownOnly ? actorUserId : (Guid?)null },
            cancellationToken: ct));
        return rows > 0;
    }

    public async Task<FeedbackTicketResolution> ResolveTicketAsync(long number, CancellationToken ct = default)
    {
        var ticketId = await _ticketLookup.GetIdByNumberAsync(number, ct);
        if (ticketId is null) return new FeedbackTicketResolution(number, null, null);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var subject = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT subject FROM tickets WHERE id = @ticketId",
            new { ticketId }, cancellationToken: ct));
        return new FeedbackTicketResolution(number, ticketId, subject);
    }

    private static async Task<bool> IsActiveStaffAsync(NpgsqlConnection conn, Guid userId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(
                SELECT 1 FROM users
                WHERE id = @userId AND is_active = TRUE AND role_name IN ('Agent','Admin'))
            """,
            new { userId }, cancellationToken: ct));
}
