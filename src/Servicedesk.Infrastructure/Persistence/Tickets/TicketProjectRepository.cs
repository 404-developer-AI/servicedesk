using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// v0.0.104 — project tickets. A project ticket is a normal ticket with
/// is_project = TRUE; other tickets point at it via project_ticket_id
/// (one project per ticket, no nesting). All mutations here write their
/// own internal timeline event; SignalR + audit stay in the endpoint.
public interface ITicketProjectRepository
{
    /// Flags an existing ticket as a project. Refused when the ticket is
    /// merged, already a project, or itself linked to a project.
    Task<ProjectMutationResult> ConvertToProjectAsync(Guid ticketId, Guid actorUserId, CancellationToken ct);

    /// Clears the project flag again. Refused while any ticket is still
    /// linked to this project (unlink first — the escape hatch, not a force).
    Task<ProjectMutationResult> RevertProjectAsync(Guid ticketId, Guid actorUserId, CancellationToken ct);

    /// Links a normal ticket to a project. Re-linking to a different
    /// project overwrites (metadata carries the previous project). Also
    /// stamps project_prompt_dismissed_utc so the link prompt never
    /// re-asks on this ticket, even after a later unlink.
    Task<ProjectMutationResult> LinkToProjectAsync(Guid ticketId, Guid projectTicketId, Guid actorUserId, CancellationToken ct);

    /// Clears the project link. Returns the previous project id in the
    /// result so the endpoint can push SignalR to that ticket group too.
    Task<ProjectMutationResult> UnlinkFromProjectAsync(Guid ticketId, Guid actorUserId, CancellationToken ct);

    /// Persists the manual priority order of the project's linked tickets.
    /// Ids not linked to this project are ignored; missing ids keep their
    /// old position value (they sort after the reordered ones).
    Task<bool> ReorderAsync(Guid projectTicketId, IReadOnlyList<Guid> orderedTicketIds, CancellationToken ct);

    /// Everything the project panel renders, in one round-trip: linked
    /// tickets (open first, then by manual order) and the per-ticket ×
    /// per-task time rollup over the project ticket itself + its linked
    /// tickets. Non-admin viewers only see tickets in queues they can
    /// access; hiddenTicketCount says how many rows were withheld so the
    /// panel can say so instead of silently under-reporting.
    Task<ProjectOverview?> GetOverviewAsync(
        Guid projectTicketId, IReadOnlyCollection<Guid>? accessibleQueueIds, CancellationToken ct);

    /// Open projects on the ticket's company for the link-to-project
    /// prompt. Empty when the prompt does not apply (ticket is a project,
    /// already linked, prompt dismissed, no company, ticket closed, …).
    Task<IReadOnlyList<ProjectPromptCandidate>> GetPromptCandidatesAsync(Guid ticketId, CancellationToken ct);

    /// Remembers "no" on the link prompt (idempotent).
    Task DismissPromptAsync(Guid ticketId, CancellationToken ct);
}

public enum ProjectFailureReason
{
    NotFound,
    IsMerged,
    AlreadyProject,
    NotAProject,
    LinkedToProject,
    HasLinkedTickets,
    TargetNotFound,
    TargetNotProject,
    TargetIsMerged,
    SourceIsProject,
    SameTicket,
    NotLinked,
}

public sealed record ProjectMutationResult(
    bool Success,
    ProjectFailureReason? FailureReason,
    Guid? PreviousProjectTicketId = null,
    long? ProjectNumber = null);

public sealed class ProjectLinkedTicketRow
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public string Subject { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime? LinkedUtc { get; set; }
    public string StatusName { get; set; } = string.Empty;
    public string StatusColor { get; set; } = string.Empty;
    public string StatusStateCategory { get; set; } = string.Empty;
    public string PriorityName { get; set; } = string.Empty;
    public string? AssigneeName { get; set; }
    public string? RequesterName { get; set; }
    public string? RequesterEmail { get; set; }
    public string QueueName { get; set; } = string.Empty;
}

public sealed class ProjectTimeRow
{
    public Guid TicketId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public bool TaskIsAbsence { get; set; }
    public int Minutes { get; set; }
}

public sealed record ProjectOverview(
    IReadOnlyList<ProjectLinkedTicketRow> Tickets,
    IReadOnlyList<ProjectTimeRow> TimeRows,
    int HiddenTicketCount);

public sealed class ProjectPromptCandidate
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public string Subject { get; set; } = string.Empty;
    public Guid QueueId { get; set; }
}

public sealed class TicketProjectRepository : ITicketProjectRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketProjectRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    private sealed class ProjectStateRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public bool IsProject { get; set; }
        public Guid? ProjectTicketId { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
    }

    private const string StateSql = """
        SELECT id AS Id, number AS Number, is_project AS IsProject,
               project_ticket_id AS ProjectTicketId,
               merged_into_ticket_id AS MergedIntoTicketId
        FROM tickets
        WHERE id = @id AND is_deleted = FALSE
        FOR UPDATE
        """;

    public async Task<ProjectMutationResult> ConvertToProjectAsync(
        Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<ProjectStateRow>(
            new CommandDefinition(StateSql, new { id = ticketId }, tx, cancellationToken: ct));
        if (row is null) return new ProjectMutationResult(false, ProjectFailureReason.NotFound);
        if (row.MergedIntoTicketId is not null) return new ProjectMutationResult(false, ProjectFailureReason.IsMerged);
        if (row.IsProject) return new ProjectMutationResult(false, ProjectFailureReason.AlreadyProject);
        if (row.ProjectTicketId is not null) return new ProjectMutationResult(false, ProjectFailureReason.LinkedToProject);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET is_project = TRUE, updated_utc = now() WHERE id = @ticketId",
            new { ticketId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ProjectConverted', @actorUserId, '{}'::jsonb, TRUE)
            """,
            new { ticketId, actorUserId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new ProjectMutationResult(true, null);
    }

    public async Task<ProjectMutationResult> RevertProjectAsync(
        Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<ProjectStateRow>(
            new CommandDefinition(StateSql, new { id = ticketId }, tx, cancellationToken: ct));
        if (row is null) return new ProjectMutationResult(false, ProjectFailureReason.NotFound);
        if (!row.IsProject) return new ProjectMutationResult(false, ProjectFailureReason.NotAProject);

        var linkedCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM tickets WHERE project_ticket_id = @ticketId AND is_deleted = FALSE",
            new { ticketId }, tx, cancellationToken: ct));
        if (linkedCount > 0) return new ProjectMutationResult(false, ProjectFailureReason.HasLinkedTickets);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET is_project = FALSE, updated_utc = now() WHERE id = @ticketId",
            new { ticketId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ProjectReverted', @actorUserId, '{}'::jsonb, TRUE)
            """,
            new { ticketId, actorUserId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new ProjectMutationResult(true, null);
    }

    public async Task<ProjectMutationResult> LinkToProjectAsync(
        Guid ticketId, Guid projectTicketId, Guid actorUserId, CancellationToken ct)
    {
        if (ticketId == projectTicketId)
            return new ProjectMutationResult(false, ProjectFailureReason.SameTicket);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock in deterministic order to avoid deadlocks with a concurrent
        // link in the opposite direction.
        var firstId = ticketId.CompareTo(projectTicketId) < 0 ? ticketId : projectTicketId;
        var secondId = firstId == ticketId ? projectTicketId : ticketId;
        var first = await conn.QueryFirstOrDefaultAsync<ProjectStateRow>(
            new CommandDefinition(StateSql, new { id = firstId }, tx, cancellationToken: ct));
        var second = await conn.QueryFirstOrDefaultAsync<ProjectStateRow>(
            new CommandDefinition(StateSql, new { id = secondId }, tx, cancellationToken: ct));
        var source = firstId == ticketId ? first : second;
        var target = firstId == ticketId ? second : first;

        if (source is null) return new ProjectMutationResult(false, ProjectFailureReason.NotFound);
        if (source.MergedIntoTicketId is not null) return new ProjectMutationResult(false, ProjectFailureReason.IsMerged);
        if (source.IsProject) return new ProjectMutationResult(false, ProjectFailureReason.SourceIsProject);
        if (target is null) return new ProjectMutationResult(false, ProjectFailureReason.TargetNotFound);
        if (target.MergedIntoTicketId is not null) return new ProjectMutationResult(false, ProjectFailureReason.TargetIsMerged);
        if (!target.IsProject) return new ProjectMutationResult(false, ProjectFailureReason.TargetNotProject);

        // Append at the end of the project's manual order.
        const string updateSql = """
            UPDATE tickets
               SET project_ticket_id            = @projectTicketId,
                   project_linked_utc           = now(),
                   project_linked_by_user_id    = @actorUserId,
                   project_sort_order           = COALESCE((
                       SELECT MAX(project_sort_order) + 1 FROM tickets
                       WHERE project_ticket_id = @projectTicketId AND is_deleted = FALSE), 0),
                   project_prompt_dismissed_utc = COALESCE(project_prompt_dismissed_utc, now()),
                   updated_utc                  = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId, projectTicketId, actorUserId }, tx, cancellationToken: ct));

        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            projectTicketId,
            projectNumber = target.Number,
            previousProjectTicketId = source.ProjectTicketId,
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ProjectLinked', @actorUserId, @metadata::jsonb, TRUE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new ProjectMutationResult(true, null, source.ProjectTicketId, target.Number);
    }

    public async Task<ProjectMutationResult> UnlinkFromProjectAsync(
        Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<ProjectStateRow>(
            new CommandDefinition(StateSql, new { id = ticketId }, tx, cancellationToken: ct));
        if (row is null) return new ProjectMutationResult(false, ProjectFailureReason.NotFound);
        if (row.ProjectTicketId is not Guid previousProjectId)
            return new ProjectMutationResult(false, ProjectFailureReason.NotLinked);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tickets
               SET project_ticket_id         = NULL,
                   project_linked_utc        = NULL,
                   project_linked_by_user_id = NULL,
                   project_sort_order        = 0,
                   updated_utc               = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """,
            new { ticketId }, tx, cancellationToken: ct));

        var metadata = System.Text.Json.JsonSerializer.Serialize(
            new { previousProjectTicketId = previousProjectId });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ProjectUnlinked', @actorUserId, @metadata::jsonb, TRUE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new ProjectMutationResult(true, null, previousProjectId);
    }

    public async Task<bool> ReorderAsync(
        Guid projectTicketId, IReadOnlyList<Guid> orderedTicketIds, CancellationToken ct)
    {
        if (orderedTicketIds.Count == 0) return true;

        // One statement: position in the array becomes the sort order. Rows
        // not linked to this project are filtered by the WHERE, so a stale
        // client list can never move tickets of another project.
        const string sql = """
            UPDATE tickets t
               SET project_sort_order = x.ord - 1,
                   updated_utc        = now()
              FROM unnest(@Ids) WITH ORDINALITY AS x(id, ord)
             WHERE t.id = x.id
               AND t.project_ticket_id = @projectTicketId
               AND t.is_deleted = FALSE
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { Ids = orderedTicketIds.ToArray(), projectTicketId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<ProjectOverview?> GetOverviewAsync(
        Guid projectTicketId, IReadOnlyCollection<Guid>? accessibleQueueIds, CancellationToken ct)
    {
        var queueFilter = accessibleQueueIds is not null
            ? " AND t.queue_id = ANY(@AccessibleQueueIds)"
            : string.Empty;

        // 0: the project row itself; 1: linked tickets (queue-filtered for
        // agents); 2: total linked count (unfiltered, for hiddenTicketCount);
        // 3: minutes per ticket per task over project + visible linked rows.
        var sql = $"""
            SELECT is_project FROM tickets
            WHERE id = @projectTicketId AND is_deleted = FALSE;

            SELECT t.id                  AS Id,
                   t.number              AS Number,
                   t.subject             AS Subject,
                   t.project_sort_order  AS SortOrder,
                   t.project_linked_utc  AS LinkedUtc,
                   s.name                AS StatusName,
                   s.color               AS StatusColor,
                   s.state_category      AS StatusStateCategory,
                   p.name                AS PriorityName,
                   u.email               AS AssigneeName,
                   NULLIF(CONCAT_WS(' ', c.first_name, c.last_name), '') AS RequesterName,
                   c.email               AS RequesterEmail,
                   q.name                AS QueueName
            FROM tickets t
            JOIN statuses   s ON s.id = t.status_id
            JOIN priorities p ON p.id = t.priority_id
            JOIN contacts   c ON c.id = t.requester_contact_id
            JOIN queues     q ON q.id = t.queue_id
            LEFT JOIN users u ON u.id = t.assignee_user_id
            WHERE t.project_ticket_id = @projectTicketId AND t.is_deleted = FALSE{queueFilter}
            ORDER BY (CASE WHEN s.state_category IN ('Resolved','Closed') THEN 1 ELSE 0 END),
                     t.project_sort_order, t.number;

            SELECT COUNT(*) FROM tickets
            WHERE project_ticket_id = @projectTicketId AND is_deleted = FALSE;

            SELECT e.ticket_id   AS TicketId,
                   tk.name       AS TaskName,
                   tk.is_absence AS TaskIsAbsence,
                   SUM(e.minutes)::int AS Minutes
            FROM timesheet_entries e
            JOIN timesheet_tasks tk ON tk.id = e.task_id
            WHERE e.ticket_id = @projectTicketId
               OR e.ticket_id IN (SELECT t.id FROM tickets t
                                  WHERE t.project_ticket_id = @projectTicketId
                                    AND t.is_deleted = FALSE{queueFilter})
            GROUP BY e.ticket_id, tk.name, tk.is_absence
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql,
            new { projectTicketId, AccessibleQueueIds = accessibleQueueIds as IEnumerable<Guid> },
            cancellationToken: ct));

        var isProject = await grid.ReadFirstOrDefaultAsync<bool?>();
        var tickets = (await grid.ReadAsync<ProjectLinkedTicketRow>()).ToList();
        var totalLinked = await grid.ReadFirstAsync<int>();
        var timeRows = (await grid.ReadAsync<ProjectTimeRow>()).ToList();

        if (isProject is not true) return null;
        return new ProjectOverview(tickets, timeRows, totalLinked - tickets.Count);
    }

    public async Task<IReadOnlyList<ProjectPromptCandidate>> GetPromptCandidatesAsync(
        Guid ticketId, CancellationToken ct)
    {
        // All applicability rules live in this one query: the ticket must be
        // a plain, open, un-linked, un-prompted ticket with a company, and a
        // candidate is an open project on that same company.
        const string sql = """
            SELECT p.id AS Id, p.number AS Number, p.subject AS Subject, p.queue_id AS QueueId
            FROM tickets t
            JOIN statuses ts ON ts.id = t.status_id
            JOIN tickets  p  ON p.company_id = t.company_id
                            AND p.is_project = TRUE
                            AND p.is_deleted = FALSE
                            AND p.merged_into_ticket_id IS NULL
                            AND p.id <> t.id
            JOIN statuses ps ON ps.id = p.status_id
            WHERE t.id = @ticketId
              AND t.is_deleted = FALSE
              AND t.is_project = FALSE
              AND t.project_ticket_id IS NULL
              AND t.merged_into_ticket_id IS NULL
              AND t.project_prompt_dismissed_utc IS NULL
              AND t.company_id IS NOT NULL
              AND ts.state_category NOT IN ('Resolved','Closed')
              AND ps.state_category NOT IN ('Resolved','Closed')
            ORDER BY p.number DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ProjectPromptCandidate>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task DismissPromptAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tickets SET project_prompt_dismissed_utc = now()
            WHERE id = @ticketId AND project_prompt_dismissed_utc IS NULL AND is_deleted = FALSE
            """,
            new { ticketId }, cancellationToken: ct));
    }
}
