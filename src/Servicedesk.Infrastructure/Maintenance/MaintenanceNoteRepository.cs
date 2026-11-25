using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Maintenance;

/// Whose notes these are: one plan member (company + contact + label).
public sealed class MaintenanceNoteSubject
{
    public Guid MemberId { get; set; }
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? ContactName { get; set; }
    public string Label { get; set; } = string.Empty;
}

public sealed class MaintenanceNoteRow
{
    public Guid Id { get; set; }
    public string BodyHtml { get; set; } = string.Empty;
    public Guid? TicketId { get; set; }
    /// Null when the caller has no access to that ticket's queue (the note
    /// itself stays visible; only the link is masked).
    public long? TicketNumber { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? UpdatedByEmail { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

public sealed class MaintenanceNoteRevisionRow
{
    public Guid NoteId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public long? TicketNumber { get; set; }
    public string? ByEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// v0.1.67 — maintenance notes per plan member (owner decisions: per member,
/// anyone with access to one of the member's tickets may add and edit, every
/// change in the history, soft delete, rich text, searchable). ONE live set
/// shown on every round ticket of the member and in the planner — never
/// copied onto a ticket (no timeline snapshot either, owner decision: a copy
/// would be edited in the wrong place). Each write is one guarded statement
/// that also records the revision.
public interface IMaintenanceNoteRepository
{
    /// The member a planner ticket belongs to; null for any other ticket.
    Task<MaintenanceNoteSubject?> SubjectForTicketAsync(Guid ticketId, CancellationToken ct);
    Task<MaintenanceNoteSubject?> SubjectForMemberAsync(Guid memberId, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceNoteRow>> ListAsync(Guid memberId, QueueAccessScope scope, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceNoteRevisionRow>> HistoryAsync(Guid memberId, QueueAccessScope scope, CancellationToken ct);
    Task<Guid> AddAsync(Guid memberId, string bodyHtml, Guid? ticketId, Guid userId, CancellationToken ct);
    /// False when the note does not exist, is deleted or belongs to another member.
    Task<bool> UpdateAsync(Guid memberId, Guid noteId, string bodyHtml, Guid? ticketId, Guid userId, CancellationToken ct);
    Task<bool> DeleteAsync(Guid memberId, Guid noteId, Guid? ticketId, Guid userId, CancellationToken ct);
}

public sealed class MaintenanceNoteRepository : IMaintenanceNoteRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public MaintenanceNoteRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SubjectSelect = """
        SELECT m.id AS MemberId, p.id AS PlanId, p.name AS PlanName, co.name AS CompanyName,
               NULLIF(trim(COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')), '') AS ContactName,
               m.label AS Label
        FROM maintenance_plan_members m
        JOIN maintenance_plans p ON p.id = m.plan_id
        LEFT JOIN companies co ON co.id = m.company_id
        LEFT JOIN contacts c ON c.id = m.contact_id
        """;

    public async Task<MaintenanceNoteSubject?> SubjectForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MaintenanceNoteSubject>(new CommandDefinition($"""
            {SubjectSelect}
            JOIN maintenance_round_tickets rt ON rt.member_id = m.id
            WHERE rt.ticket_id = @ticketId
            LIMIT 1
            """, new { ticketId }, cancellationToken: ct));
    }

    public async Task<MaintenanceNoteSubject?> SubjectForMemberAsync(Guid memberId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MaintenanceNoteSubject>(new CommandDefinition(
            $"{SubjectSelect} WHERE m.id = @memberId", new { memberId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MaintenanceNoteRow>> ListAsync(Guid memberId, QueueAccessScope scope, CancellationToken ct)
    {
        var sql = $"""
            SELECT n.id AS Id, n.body_html AS BodyHtml, n.ticket_id AS TicketId,
                   CASE WHEN t.id IS NOT NULL AND {scope.SqlPredicate("t.queue_id")} THEN t.number END AS TicketNumber,
                   cu.email::text AS CreatedByEmail, n.created_utc AS CreatedUtc,
                   uu.email::text AS UpdatedByEmail, n.updated_utc AS UpdatedUtc
            FROM maintenance_member_notes n
            LEFT JOIN tickets t ON t.id = n.ticket_id
            LEFT JOIN users cu ON cu.id = n.created_by_user_id
            LEFT JOIN users uu ON uu.id = n.updated_by_user_id
            WHERE n.member_id = @memberId AND NOT n.is_deleted
            ORDER BY n.created_utc, n.id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<MaintenanceNoteRow>(new CommandDefinition(
            sql, scope.Parameters(new { memberId }), cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<MaintenanceNoteRevisionRow>> HistoryAsync(Guid memberId, QueueAccessScope scope, CancellationToken ct)
    {
        var sql = $"""
            SELECT r.note_id AS NoteId, r.action AS Action, r.body_html AS BodyHtml,
                   CASE WHEN t.id IS NOT NULL AND {scope.SqlPredicate("t.queue_id")} THEN t.number END AS TicketNumber,
                   u.email::text AS ByEmail, r.created_utc AS CreatedUtc
            FROM maintenance_member_note_revisions r
            JOIN maintenance_member_notes n ON n.id = r.note_id
            LEFT JOIN tickets t ON t.id = r.ticket_id
            LEFT JOIN users u ON u.id = r.by_user_id
            WHERE n.member_id = @memberId
            ORDER BY r.created_utc DESC, r.id
            LIMIT 500
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<MaintenanceNoteRevisionRow>(new CommandDefinition(
            sql, scope.Parameters(new { memberId }), cancellationToken: ct))).ToList();
    }

    public async Task<Guid> AddAsync(Guid memberId, string bodyHtml, Guid? ticketId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            WITH note AS (
                INSERT INTO maintenance_member_notes (member_id, body_html, ticket_id, created_by_user_id)
                VALUES (@memberId, @bodyHtml, @ticketId, @userId)
                RETURNING id, body_html
            ), rev AS (
                INSERT INTO maintenance_member_note_revisions (note_id, action, body_html, ticket_id, by_user_id)
                SELECT id, 'Added', body_html, @ticketId, @userId FROM note
            )
            SELECT id FROM note
            """, new { memberId, bodyHtml, ticketId, userId }, cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(Guid memberId, Guid noteId, string bodyHtml, Guid? ticketId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            WITH upd AS (
                UPDATE maintenance_member_notes SET body_html = @bodyHtml, updated_by_user_id = @userId, updated_utc = now()
                WHERE id = @noteId AND member_id = @memberId AND NOT is_deleted
                RETURNING id, body_html
            ), rev AS (
                INSERT INTO maintenance_member_note_revisions (note_id, action, body_html, ticket_id, by_user_id)
                SELECT id, 'Edited', body_html, @ticketId, @userId FROM upd
            )
            SELECT COUNT(*)::int FROM upd
            """, new { memberId, noteId, bodyHtml, ticketId, userId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteAsync(Guid memberId, Guid noteId, Guid? ticketId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            WITH del AS (
                UPDATE maintenance_member_notes SET is_deleted = TRUE, updated_by_user_id = @userId, updated_utc = now()
                WHERE id = @noteId AND member_id = @memberId AND NOT is_deleted
                RETURNING id, body_html
            ), rev AS (
                INSERT INTO maintenance_member_note_revisions (note_id, action, body_html, ticket_id, by_user_id)
                SELECT id, 'Deleted', body_html, @ticketId, @userId FROM del
            )
            SELECT COUNT(*)::int FROM del
            """, new { memberId, noteId, ticketId, userId }, cancellationToken: ct)) > 0;
    }
}
