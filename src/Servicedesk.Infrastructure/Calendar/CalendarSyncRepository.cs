using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Calendar;

public static class CalendarEventStatus
{
    public const string Pending = "Pending";
    public const string Synced = "Synced";
    public const string Failed = "Failed";
    public const string CancelPending = "CancelPending";
    public const string Cancelled = "Cancelled";
}

/// One appointment a form keeps in sync with Outlook.
public sealed class CalendarEventRow
{
    public Guid Id { get; set; }
    public Guid FormId { get; set; }
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public bool IsTeams { get; set; }
    public string Status { get; set; } = CalendarEventStatus.Pending;
    public bool Recreate { get; set; }
    public string? GraphEventId { get; set; }
    public string? ICalUId { get; set; }
    public string? WebLink { get; set; }
    public string? JoinUrl { get; set; }
    public int SyncedVersion { get; set; }
    public string SyncedAttachments { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    // Joined for display / processing.
    public string UserEmail { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? CalendarId { get; set; }
}

/// The form a calendar sync works from (live answers + its template's mapping).
public sealed class CalendarFormContext
{
    public Guid FormId { get; set; }
    public Guid TicketId { get; set; }
    public long TicketNumber { get; set; }
    public string TicketSubject { get; set; } = string.Empty;
    public bool TicketIsProject { get; set; }
    public string FormName { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool Deleted { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
    public string AnswersJson { get; set; } = string.Empty;
    public string? OutlookJson { get; set; }
}

public sealed record CalendarUser(Guid Id, string Email, string Name, string? CalendarId, bool IsActive);

/// The desired appointment for one user on a form.
public sealed record CalendarPlanEntry(Guid UserId, bool IsTeams);

public interface ICalendarSyncRepository
{
    Task<CalendarFormContext?> GetFormContextAsync(Guid formId, CancellationToken ct);
    Task<IReadOnlyList<CalendarEventRow>> ListForFormAsync(Guid formId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, CalendarUser>> GetUsersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// Makes the rows match the plan in one transaction: rows no longer
    /// wanted are cancelled (or dropped when never written to Outlook), wanted
    /// rows are (re)queued. A row whose Teams-ness changed is marked recreate.
    Task ApplyPlanAsync(Guid formId, Guid ticketId, IReadOnlyList<CalendarPlanEntry> plan, CancellationToken ct);
    Task<IReadOnlyList<CalendarEventRow>> ListDueAsync(int maxAttempts, int limit, CancellationToken ct);
    Task MarkSyncedAsync(Guid rowId, CalendarEventResult result, int version, string attachmentsKey, CancellationToken ct);
    Task MarkFailedAsync(Guid rowId, string error, DateTime? nextAttemptUtc, CancellationToken ct);
    Task MarkCancelledAsync(Guid rowId, CancellationToken ct);
    /// The Outlook event is gone (deleted there): forget its id so the next
    /// pass creates it again.
    Task ForgetEventAsync(Guid rowId, CancellationToken ct);
    /// Manual retry: failed rows of a form back in the queue.
    Task<int> RetryFailedAsync(Guid formId, CancellationToken ct);
    /// A system note on the ticket timeline (internal) — e.g. "Teams
    /// invitation sent to …", so outbound invitations are on record.
    Task AddTimelineNoteAsync(Guid ticketId, string text, string metadataJson, CancellationToken ct);
}

public sealed class CalendarSyncRepository : ICalendarSyncRepository
{
    private readonly NpgsqlDataSource _ds;

    public CalendarSyncRepository(NpgsqlDataSource ds) => _ds = ds;

    private const string RowSelect = """
        SELECT e.id AS Id, e.form_id AS FormId, e.ticket_id AS TicketId, e.user_id AS UserId,
               e.is_teams AS IsTeams, e.status AS Status, e.recreate AS Recreate,
               e.graph_event_id AS GraphEventId, e.ical_uid AS ICalUId, e.web_link AS WebLink, e.join_url AS JoinUrl,
               e.synced_version AS SyncedVersion, e.synced_attachments AS SyncedAttachments,
               e.attempts AS Attempts, e.next_attempt_utc AS NextAttemptUtc, e.last_error AS LastError,
               e.updated_utc AS UpdatedUtc,
               u.email::text AS UserEmail,
               COALESCE(NULLIF(u.display_name, ''), u.email::text) AS UserName,
               u.calendar_id AS CalendarId
        FROM ticket_form_calendar_events e
        JOIN users u ON u.id = e.user_id
        """;

    public async Task<CalendarFormContext?> GetFormContextAsync(Guid formId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<CalendarFormContext>(new CommandDefinition("""
            SELECT f.id AS FormId, f.ticket_id AS TicketId, t.number AS TicketNumber, t.subject AS TicketSubject,
                   t.is_project AS TicketIsProject, f.name AS FormName, f.version AS Version,
                   (f.deleted_utc IS NOT NULL OR t.is_deleted) AS Deleted,
                   f.template_snapshot::text AS SnapshotJson, f.answers::text AS AnswersJson,
                   ft.outlook_mapping::text AS OutlookJson
            FROM ticket_forms f
            JOIN tickets t ON t.id = f.ticket_id
            LEFT JOIN form_templates ft ON ft.id = f.template_id
            WHERE f.id = @formId
            """, new { formId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CalendarEventRow>> ListForFormAsync(Guid formId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<CalendarEventRow>(new CommandDefinition(
            RowSelect + " WHERE e.form_id = @formId ORDER BY e.created_utc, e.id", new { formId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, CalendarUser>> GetUsersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, CalendarUser>();
        await using var c = await _ds.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<UserRow>(new CommandDefinition("""
            SELECT id AS Id, email::text AS Email, COALESCE(NULLIF(display_name, ''), email::text) AS Name,
                   calendar_id AS CalendarId, is_active AS IsActive
            FROM users WHERE id = ANY(@ids) AND role_name IN ('Agent', 'Admin')
            """, new { ids = ids.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => new CalendarUser(r.Id, r.Email, r.Name, r.CalendarId, r.IsActive));
    }

    public async Task ApplyPlanAsync(Guid formId, Guid ticketId, IReadOnlyList<CalendarPlanEntry> plan, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var existing = (await c.QueryAsync<(Guid Id, Guid UserId, bool IsTeams, string? GraphEventId, string Status)>(
            new CommandDefinition("""
                SELECT id, user_id, is_teams, graph_event_id, status
                FROM ticket_form_calendar_events WHERE form_id = @formId FOR UPDATE
                """, new { formId }, tx, cancellationToken: ct))).ToList();

        var wanted = plan.ToDictionary(p => p.UserId);
        foreach (var row in existing.Where(r => !wanted.ContainsKey(r.UserId)))
        {
            if (row.Status == CalendarEventStatus.Cancelled) continue;
            if (row.GraphEventId is null)
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM ticket_form_calendar_events WHERE id = @id", new { id = row.Id }, tx, cancellationToken: ct));
            else
                await c.ExecuteAsync(new CommandDefinition("""
                    UPDATE ticket_form_calendar_events
                       SET status = 'CancelPending', attempts = 0, next_attempt_utc = NULL, last_error = '', updated_utc = now()
                     WHERE id = @id
                    """, new { id = row.Id }, tx, cancellationToken: ct));
        }

        foreach (var p in plan)
        {
            var row = existing.FirstOrDefault(r => r.UserId == p.UserId);
            if (row.Id == Guid.Empty)
            {
                await c.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO ticket_form_calendar_events (form_id, ticket_id, user_id, is_teams, status)
                    VALUES (@formId, @ticketId, @userId, @isTeams, 'Pending')
                    ON CONFLICT (form_id, user_id) DO NOTHING
                    """, new { formId, ticketId, userId = p.UserId, isTeams = p.IsTeams }, tx, cancellationToken: ct));
                continue;
            }
            // A Teams meeting can't become a plain appointment in place (or
            // back): the old one is cancelled and a new one created.
            var recreate = row.GraphEventId is not null && (row.IsTeams != p.IsTeams || row.Status == CalendarEventStatus.Cancelled);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE ticket_form_calendar_events
                   SET status = 'Pending', is_teams = @isTeams,
                       recreate = recreate OR @recreate,
                       graph_event_id = CASE WHEN status = 'Cancelled' THEN NULL ELSE graph_event_id END,
                       attempts = 0, next_attempt_utc = NULL, last_error = '', updated_utc = now()
                 WHERE id = @id
                """, new { id = row.Id, isTeams = p.IsTeams, recreate = recreate && row.Status != CalendarEventStatus.Cancelled },
                tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CalendarEventRow>> ListDueAsync(int maxAttempts, int limit, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<CalendarEventRow>(new CommandDefinition(RowSelect + """
             WHERE e.status IN ('Pending','Failed','CancelPending')
               AND (e.status <> 'Failed' OR e.attempts < @maxAttempts)
               AND (e.next_attempt_utc IS NULL OR e.next_attempt_utc <= now())
             ORDER BY e.next_attempt_utc NULLS FIRST, e.updated_utc
             LIMIT @limit
            """, new { maxAttempts, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task MarkSyncedAsync(Guid rowId, CalendarEventResult result, int version, string attachmentsKey, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_form_calendar_events
               SET status = 'Synced', recreate = FALSE, graph_event_id = @eventId,
                   ical_uid = COALESCE(@ical, ical_uid), web_link = COALESCE(@link, web_link), join_url = COALESCE(@join, join_url),
                   synced_version = @version, synced_attachments = @attachmentsKey,
                   attempts = 0, next_attempt_utc = NULL, last_error = '', updated_utc = now()
             WHERE id = @rowId
            """, new { rowId, eventId = result.Id, ical = result.ICalUId, link = result.WebLink, join = result.JoinUrl, version, attachmentsKey },
            cancellationToken: ct));
    }

    public async Task MarkFailedAsync(Guid rowId, string error, DateTime? nextAttemptUtc, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_form_calendar_events
               SET status = CASE WHEN status = 'CancelPending' THEN 'CancelPending' ELSE 'Failed' END,
                   attempts = attempts + 1, next_attempt_utc = @next, last_error = @error, updated_utc = now()
             WHERE id = @rowId
            """, new { rowId, error = error.Length > 1000 ? error[..1000] : error, next = nextAttemptUtc }, cancellationToken: ct));
    }

    public async Task MarkCancelledAsync(Guid rowId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_form_calendar_events
               SET status = 'Cancelled', recreate = FALSE, graph_event_id = NULL, join_url = NULL,
                   attempts = 0, next_attempt_utc = NULL, last_error = '', updated_utc = now()
             WHERE id = @rowId
            """, new { rowId }, cancellationToken: ct));
    }

    public async Task ForgetEventAsync(Guid rowId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_form_calendar_events
               SET graph_event_id = NULL, recreate = FALSE, synced_attachments = '', updated_utc = now()
             WHERE id = @rowId
            """, new { rowId }, cancellationToken: ct));
    }

    public async Task<int> RetryFailedAsync(Guid formId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_form_calendar_events
               SET status = CASE WHEN status = 'CancelPending' THEN 'CancelPending' ELSE 'Pending' END,
                   attempts = 0, next_attempt_utc = NULL, updated_utc = now()
             WHERE form_id = @formId AND (status = 'Failed' OR (status = 'CancelPending' AND last_error <> ''))
            """, new { formId }, cancellationToken: ct));
    }

    public async Task AddTimelineNoteAsync(Guid ticketId, string text, string metadataJson, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal, body_text)
            VALUES (@ticketId, 'SystemNote', NULL, @metadata::jsonb, TRUE, @text)
            """, new { ticketId, text, metadata = metadataJson }, cancellationToken: ct));
    }

    private sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? CalendarId { get; set; }
        public bool IsActive { get; set; }
    }
}
