using Dapper;
using Npgsql;

namespace Servicedesk.Api.Tests.Database;

/// Minimal row builders for database tests. Every name, slug and e-mail is
/// made unique per call so tests sharing the one test database never
/// collide. Values are fictitious (public repository).
public sealed class DbSeed
{
    private readonly NpgsqlDataSource _ds;

    public DbSeed(NpgsqlDataSource ds) => _ds = ds;

    private static string U() => Guid.NewGuid().ToString("N")[..12];

    public async Task<Guid> QueueAsync(string name = "Queue")
    {
        await using var c = await _ds.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO queues (name, slug) VALUES (@name, @slug) RETURNING id",
            new { name = $"{name} {U()}", slug = $"q-{U()}" });
    }

    public async Task<Guid> StatusAsync(string stateCategory = "Open")
    {
        await using var c = await _ds.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO statuses (name, slug, state_category) VALUES (@name, @slug, @cat) RETURNING id",
            new { name = $"Status {U()}", slug = $"s-{U()}", cat = stateCategory });
    }

    public async Task<Guid> PriorityAsync()
    {
        await using var c = await _ds.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO priorities (name, slug) VALUES (@name, @slug) RETURNING id",
            new { name = $"Priority {U()}", slug = $"p-{U()}" });
    }

    public async Task<Guid> CompanyAsync(string name = "Example Company")
    {
        await using var c = await _ds.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO companies (name, code) VALUES (@name, @code) RETURNING id",
            new { name = $"{name} {U()}", code = $"C{U()}" });
    }

    public async Task<Guid> ContactAsync()
    {
        await using var c = await _ds.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>(
            "INSERT INTO contacts (email, first_name, last_name) VALUES (@email, 'Jane', 'Example') RETURNING id",
            new { email = $"jane.{U()}@example.test" });
    }

    public async Task<Guid> TicketAsync(
        string subject, Guid requesterContactId, Guid queueId, Guid statusId, Guid priorityId,
        Guid? companyId = null, bool isProject = false, bool isDeleted = false)
    {
        await using var c = await _ds.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO tickets (subject, requester_contact_id, queue_id, status_id, priority_id,
                                 company_id, is_project, is_deleted, ticket_type_id)
            VALUES (@subject, @requesterContactId, @queueId, @statusId, @priorityId,
                    @companyId, @isProject, @isDeleted,
                    (SELECT id FROM ticket_types WHERE code = 'support'))
            RETURNING id
            """,
            new { subject, requesterContactId, queueId, statusId, priorityId, companyId, isProject, isDeleted });
        await c.ExecuteAsync(
            "INSERT INTO ticket_bodies (ticket_id, body_text) VALUES (@id, 'Description') ON CONFLICT (ticket_id) DO NOTHING",
            new { id });
        return id;
    }

    public async Task<long> EventAsync(Guid ticketId, string eventType, bool isInternal, string body = "Event body")
    {
        await using var c = await _ds.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO ticket_events (ticket_id, event_type, body_text, is_internal)
            VALUES (@ticketId, @eventType, @body, @isInternal)
            RETURNING id
            """,
            new { ticketId, eventType, body, isInternal });
    }

    /// One queue + status + priority, the minimum a ticket needs.
    public async Task<(Guid Queue, Guid Status, Guid Priority)> TaxonomyAsync()
        => (await QueueAsync(), await StatusAsync(), await PriorityAsync());
}
