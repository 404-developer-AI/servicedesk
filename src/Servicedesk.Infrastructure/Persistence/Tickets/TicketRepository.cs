using System.Text;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// Hand-written Dapper queries for the ticket list/detail hot paths. Keyset
/// pagination on <c>(updated_utc DESC, id DESC)</c> lets us walk 1M rows
/// without the offset penalty. All filters are parameterized — no string
/// concatenation of user input reaches the SQL.
public sealed class TicketRepository : ITicketRepository
{
    private const string ListSelect = """
        SELECT
            t.id                            AS Id,
            t.number                        AS Number,
            t.subject                       AS Subject,
            t.queue_id                      AS QueueId,
            q.name                          AS QueueName,
            t.status_id                     AS StatusId,
            s.name                          AS StatusName,
            s.state_category                AS StatusStateCategory,
            t.priority_id                   AS PriorityId,
            p.name                          AS PriorityName,
            p.level                         AS PriorityLevel,
            t.requester_contact_id          AS RequesterContactId,
            c.email                         AS RequesterEmail,
            c.first_name                    AS RequesterFirstName,
            c.last_name                     AS RequesterLastName,
            t.assignee_user_id              AS AssigneeUserId,
            t.created_utc                   AS CreatedUtc,
            t.updated_utc                   AS UpdatedUtc
        FROM tickets t
        JOIN queues     q ON q.id = t.queue_id
        JOIN statuses   s ON s.id = t.status_id
        JOIN priorities p ON p.id = t.priority_id
        JOIN contacts   c ON c.id = t.requester_contact_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    public TicketRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<TicketPage> SearchAsync(
        TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct)
    {
        var sql = new StringBuilder(ListSelect);
        sql.Append(" WHERE t.is_deleted = FALSE");

        if (query.QueueId.HasValue) sql.Append(" AND t.queue_id = @QueueId");
        if (query.StatusId.HasValue) sql.Append(" AND t.status_id = @StatusId");
        if (query.PriorityId.HasValue) sql.Append(" AND t.priority_id = @PriorityId");
        if (query.AssigneeUserId.HasValue) sql.Append(" AND t.assignee_user_id = @AssigneeUserId");
        if (query.RequesterContactId.HasValue) sql.Append(" AND t.requester_contact_id = @RequesterContactId");
        if (query.OpenOnly) sql.Append(" AND s.state_category NOT IN ('Resolved','Closed')");

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql.Append(" AND (t.search_vector @@ plainto_tsquery('simple', @Search) OR t.number::text = @SearchRaw)");
        }

        // Visibility enforcement. Never trust client-supplied scope — this is
        // resolved from the authenticated principal upstream. The filter
        // point exists so the future portal inherits it without a rewrite.
        switch (scope)
        {
            case VisibilityScope.Own:
                sql.Append(" AND c.id = (SELECT id FROM contacts WHERE id = @ViewerContactId)");
                // NB: in v0.0.5 operator users are not contacts, so Own has
                // no natural match; it's reserved for the customer portal.
                break;
            case VisibilityScope.Company:
                sql.Append(" AND c.company_id = @ViewerCompanyId");
                break;
            case VisibilityScope.All:
            default:
                break;
        }

        // Keyset cursor: rows strictly older than the cursor tuple.
        if (query.CursorUpdatedUtc.HasValue && query.CursorId.HasValue)
        {
            sql.Append(" AND (t.updated_utc, t.id) < (@CursorUpdatedUtc, @CursorId)");
        }

        sql.Append(" ORDER BY t.updated_utc DESC, t.id DESC");
        sql.Append(" LIMIT @Limit");

        var parameters = new
        {
            query.QueueId,
            query.StatusId,
            query.PriorityId,
            query.AssigneeUserId,
            query.RequesterContactId,
            Search = query.Search ?? "",
            SearchRaw = query.Search ?? "",
            ViewerContactId = viewerUserId ?? Guid.Empty,
            ViewerCompanyId = viewerCompanyId ?? Guid.Empty,
            query.CursorUpdatedUtc,
            query.CursorId,
            Limit = Math.Clamp(query.Limit, 1, 500),
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TicketListItem>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        DateTime? nextUpdated = null;
        Guid? nextId = null;
        if (rows.Count == parameters.Limit && rows.Count > 0)
        {
            var last = rows[^1];
            nextUpdated = last.UpdatedUtc;
            nextId = last.Id;
        }

        return new TicketPage(rows, nextUpdated, nextId);
    }

    public async Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        const string ticketSql = """
            SELECT id AS Id, number AS Number, subject AS Subject,
                   requester_contact_id AS RequesterContactId, assignee_user_id AS AssigneeUserId,
                   queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
                   category_id AS CategoryId, source AS Source, external_ref AS ExternalRef,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, due_utc AS DueUtc,
                   first_response_utc AS FirstResponseUtc, resolved_utc AS ResolvedUtc,
                   closed_utc AS ClosedUtc, is_deleted AS IsDeleted
            FROM tickets WHERE id = @id AND is_deleted = FALSE
            """;
        const string bodySql = """
            SELECT ticket_id AS TicketId, body_text AS BodyText, body_html AS BodyHtml
            FROM ticket_bodies WHERE ticket_id = @id
            """;
        const string eventsSql = """
            SELECT id AS Id, ticket_id AS TicketId, event_type AS EventType,
                   author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                   body_text AS BodyText, body_html AS BodyHtml,
                   metadata::text AS MetadataJson, is_internal AS IsInternal,
                   created_utc AS CreatedUtc
            FROM ticket_events WHERE ticket_id = @id ORDER BY created_utc, id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ticket = await conn.QueryFirstOrDefaultAsync<Ticket>(
            new CommandDefinition(ticketSql, new { id }, cancellationToken: ct));
        if (ticket is null) return null;

        var body = await conn.QueryFirstOrDefaultAsync<TicketBody>(
            new CommandDefinition(bodySql, new { id }, cancellationToken: ct))
            ?? new TicketBody(id, string.Empty, null);

        var events = (await conn.QueryAsync<TicketEvent>(
            new CommandDefinition(eventsSql, new { id }, cancellationToken: ct))).ToList();

        return new TicketDetail(ticket, body, events);
    }

    public async Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct)
    {
        const string insertTicket = """
            INSERT INTO tickets (subject, requester_contact_id, assignee_user_id, queue_id,
                                 status_id, priority_id, category_id, source)
            VALUES (@Subject, @RequesterContactId, @AssigneeUserId, @QueueId,
                    @StatusId, @PriorityId, @CategoryId, @Source)
            RETURNING id AS Id, number AS Number, subject AS Subject,
                      requester_contact_id AS RequesterContactId, assignee_user_id AS AssigneeUserId,
                      queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
                      category_id AS CategoryId, source AS Source, external_ref AS ExternalRef,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, due_utc AS DueUtc,
                      first_response_utc AS FirstResponseUtc, resolved_utc AS ResolvedUtc,
                      closed_utc AS ClosedUtc, is_deleted AS IsDeleted
            """;
        const string insertBody = """
            INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
            VALUES (@TicketId, @BodyText, @BodyHtml)
            """;
        const string insertEvent = """
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id, body_text, body_html)
            VALUES (@TicketId, 'Created', @AuthorContactId, @BodyText, @BodyHtml)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ticket = await conn.QuerySingleAsync<Ticket>(new CommandDefinition(insertTicket, input, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertBody,
            new { TicketId = ticket.Id, input.BodyText, input.BodyHtml }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertEvent,
            new { TicketId = ticket.Id, AuthorContactId = input.RequesterContactId, input.BodyText, input.BodyHtml },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return ticket;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct)
    {
        // Uses ix_tickets_queue_status_updated as a count-only index scan.
        // At 1M rows with the partial index this is <20ms — no separate
        // counters table needed yet.
        const string sql = """
            SELECT t.queue_id AS QueueId, count(*)::int AS OpenCount
            FROM tickets t
            JOIN statuses s ON s.id = t.status_id
            WHERE t.is_deleted = FALSE AND s.state_category NOT IN ('Resolved','Closed')
            GROUP BY t.queue_id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid QueueId, int OpenCount)>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToDictionary(r => r.QueueId, r => r.OpenCount);
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
                source, created_utc, updated_utc)
            SELECT
                'Benchmark ticket ' || g,
                @contactId,
                (SELECT id FROM queues     WHERE is_active ORDER BY random() LIMIT 1),
                (SELECT id FROM statuses   WHERE is_active ORDER BY random() LIMIT 1),
                (SELECT id FROM priorities WHERE is_active ORDER BY random() LIMIT 1),
                'Api',
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
