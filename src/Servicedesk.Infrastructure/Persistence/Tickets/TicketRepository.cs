using Servicedesk.Infrastructure.Access;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// Hand-written Dapper queries for the ticket list/detail hot paths. Keyset
/// pagination on <c>(updated_utc DESC, id DESC)</c> lets us walk 1M rows
/// without the offset penalty. When dynamic sorting or priority float is
/// enabled, falls back to offset pagination. All filters are parameterized
/// — no string concatenation of user input reaches the SQL.
public sealed partial class TicketRepository : ITicketRepository, ITicketNumberLookup
{
    public async Task<Guid?> GetIdByNumberAsync(long number, CancellationToken ct)
    {
        const string sql = "SELECT id FROM tickets WHERE number = @number AND is_deleted = FALSE";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { number }, cancellationToken: ct));
    }

    public async Task<Guid?> GetIdByZammadNumberAsync(string zammadNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zammadNumber)) return null;
        // Imported tickets only — zammad_ticket_number is NULL for native
        // tickets. LIMIT 1 guards against a hand-edited duplicate; the import
        // enforces uniqueness via ix_tickets_zammad_id.
        const string sql = """
            SELECT id FROM tickets
            WHERE zammad_ticket_number = @zammadNumber AND is_deleted = FALSE
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { zammadNumber }, cancellationToken: ct));
    }

    public async Task<bool> IsTitleReviewedAsync(Guid ticketId, CancellationToken ct)
    {
        // Treat a missing ticket as "reviewed" so a vanished ticket can
        // never re-surface the first-open gate.
        const string sql = """
            SELECT NOT EXISTS (
                SELECT 1 FROM tickets
                WHERE id = @ticketId AND title_reviewed_utc IS NULL
            )
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
    }

    public async Task<bool> MarkTitleReviewedAsync(Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        // Atomic claim: only the first concurrent confirmation flips the
        // marker. RETURNING id is non-empty exactly for the winning caller;
        // the WHERE ... IS NULL guard makes a second run a no-op.
        const string sql = """
            UPDATE tickets
               SET title_reviewed_utc = now(),
                   title_reviewed_by_user_id = @actorUserId
             WHERE id = @ticketId AND title_reviewed_utc IS NULL
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var claimed = await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { ticketId, actorUserId }, cancellationToken: ct));
        return claimed.HasValue;
    }

    /// Whitelist mapping frontend field names to SQL column expressions.
    /// Prevents SQL injection via dynamic ORDER BY.

    private readonly NpgsqlDataSource _dataSource;

    private readonly TicketFieldWriter _fieldWriter;

    public TicketRepository(NpgsqlDataSource dataSource)
    {
        _fieldWriter = new TicketFieldWriter(dataSource);
        _dataSource = dataSource;
    }

    public async Task<int> InsertFakeBatchAsync(int count, CancellationToken ct)
    {
        // Development-only. Generates <c>count</c> realistic-ish tickets by
        // sampling seeded taxonomy rows. Uses one synthetic benchmark contact
        // (upserted so repeated runs reuse the same id). All tickets get
        // randomized queue/status/priority from the active taxonomy set so
        // the partial index and per-queue filters see realistic distributions.
        const string ensureContact = """
            INSERT INTO contacts (email, first_name, last_name, company_role, is_active)
            VALUES ('benchmark@example.test', 'Bench', 'Mark', 'Member', TRUE)
            ON CONFLICT (email) DO UPDATE SET updated_utc = now()
            RETURNING id
            """;
        const string insertTickets = """
            INSERT INTO tickets (
                subject, requester_contact_id, queue_id, status_id, priority_id,
                source, ticket_type_id, created_utc, updated_utc)
            SELECT
                'Benchmark ticket ' || g,
                @contactId,
                (SELECT id FROM queues     WHERE is_active ORDER BY random() LIMIT 1),
                (SELECT id FROM statuses   WHERE is_active ORDER BY random() LIMIT 1),
                (SELECT id FROM priorities WHERE is_active ORDER BY random() LIMIT 1),
                'Api',
                (SELECT id FROM ticket_types WHERE code = 'support' LIMIT 1),
                now() - (g * interval '1 second'),
                now() - (g * interval '1 second')
            FROM generate_series(1, @count) g
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var contactId = await conn.ExecuteScalarAsync<Guid>(
            new CommandDefinition(ensureContact, cancellationToken: ct));
        return await conn.ExecuteAsync(
            new CommandDefinition(insertTickets, new { contactId, count }, cancellationToken: ct, commandTimeout: 600));
    }

}
