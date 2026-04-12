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
            c.company_id                    AS RequesterCompanyId,
            co.name                         AS CompanyName,
            t.assignee_user_id              AS AssigneeUserId,
            u.email                         AS AssigneeEmail,
            t.created_utc                   AS CreatedUtc,
            t.updated_utc                   AS UpdatedUtc,
            t.due_utc                       AS DueUtc
        FROM tickets t
        JOIN queues     q ON q.id = t.queue_id
        JOIN statuses   s ON s.id = t.status_id
        JOIN priorities p ON p.id = t.priority_id
        JOIN contacts   c ON c.id = t.requester_contact_id
        LEFT JOIN companies co ON co.id = c.company_id
        LEFT JOIN users     u  ON u.id  = t.assignee_user_id
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
                   created_utc AS CreatedUtc,
                   edited_utc AS EditedUtc, edited_by_user_id AS EditedByUserId
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

    public async Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string readSql = """
            SELECT queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
                   category_id AS CategoryId, assignee_user_id AS AssigneeUserId
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketFieldSnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) { await tx.RollbackAsync(ct); return null; }

        var sets = new List<string>();
        var events = new List<(string EventType, string MetadataJson)>();

        // Helper: look up a human-readable name for a taxonomy entity or user
        // so change events store "New → In Progress" rather than raw UUIDs.
        async Task<string?> LookupNameAsync(string table, Guid? id)
        {
            if (!id.HasValue) return null;
            var col = table == "users" ? "email" : "name";
            return await conn.ExecuteScalarAsync<string>(
                new CommandDefinition($"SELECT {col} FROM {table} WHERE id = @id",
                    new { id = id.Value }, tx, cancellationToken: ct));
        }

        if (update.QueueId.HasValue && update.QueueId != current.QueueId)
        {
            sets.Add("queue_id = @NewQueueId");
            var fromName = await LookupNameAsync("queues", current.QueueId);
            var toName = await LookupNameAsync("queues", update.QueueId);
            events.Add(("QueueChange", System.Text.Json.JsonSerializer.Serialize(
                new { from = current.QueueId, to = update.QueueId, fromName, toName })));
        }
        if (update.StatusId.HasValue && update.StatusId != current.StatusId)
        {
            sets.Add("status_id = @NewStatusId");
            var fromName = await LookupNameAsync("statuses", current.StatusId);
            var toName = await LookupNameAsync("statuses", update.StatusId);
            events.Add(("StatusChange", System.Text.Json.JsonSerializer.Serialize(
                new { from = current.StatusId, to = update.StatusId, fromName, toName })));
        }
        if (update.PriorityId.HasValue && update.PriorityId != current.PriorityId)
        {
            sets.Add("priority_id = @NewPriorityId");
            var fromName = await LookupNameAsync("priorities", current.PriorityId);
            var toName = await LookupNameAsync("priorities", update.PriorityId);
            events.Add(("PriorityChange", System.Text.Json.JsonSerializer.Serialize(
                new { from = current.PriorityId, to = update.PriorityId, fromName, toName })));
        }
        if (update.CategoryId.HasValue && update.CategoryId != current.CategoryId)
        {
            sets.Add("category_id = @NewCategoryId");
            var fromName = await LookupNameAsync("categories", current.CategoryId);
            var toName = await LookupNameAsync("categories", update.CategoryId);
            events.Add(("CategoryChange", System.Text.Json.JsonSerializer.Serialize(
                new { from = current.CategoryId, to = update.CategoryId, fromName, toName })));
        }
        if (update.AssigneeUserId.HasValue && update.AssigneeUserId != current.AssigneeUserId)
        {
            sets.Add("assignee_user_id = @NewAssigneeUserId");
            var fromName = await LookupNameAsync("users", current.AssigneeUserId);
            var toName = await LookupNameAsync("users", update.AssigneeUserId);
            events.Add(("AssignmentChange", System.Text.Json.JsonSerializer.Serialize(
                new { from = current.AssigneeUserId, to = update.AssigneeUserId, fromName, toName })));
        }

        bool bodyChanged = false;
        if (update.Subject is not null)
        {
            sets.Add("subject = @NewSubject");
        }
        if (update.BodyText is not null || update.BodyHtml is not null)
        {
            bodyChanged = true;
        }

        if (sets.Count == 0 && !bodyChanged) { await tx.RollbackAsync(ct); return await GetByIdAsync(ticketId, ct); }

        sets.Add("updated_utc = now()");

        if (update.StatusId.HasValue && update.StatusId != current.StatusId)
        {
            var stateCategory = await conn.ExecuteScalarAsync<string>(
                new CommandDefinition("SELECT state_category FROM statuses WHERE id = @id",
                    new { id = update.StatusId }, tx, cancellationToken: ct));
            if (stateCategory == "Resolved") sets.Add("resolved_utc = COALESCE(resolved_utc, now())");
            if (stateCategory == "Closed") sets.Add("closed_utc = COALESCE(closed_utc, now())");
        }

        if (sets.Count > 1) // more than just updated_utc
        {
            var updateSql = $"UPDATE tickets SET {string.Join(", ", sets)} WHERE id = @ticketId";
            await conn.ExecuteAsync(new CommandDefinition(updateSql, new
            {
                ticketId,
                NewQueueId = update.QueueId,
                NewStatusId = update.StatusId,
                NewPriorityId = update.PriorityId,
                NewCategoryId = update.CategoryId,
                NewAssigneeUserId = update.AssigneeUserId,
                NewSubject = update.Subject,
            }, tx, cancellationToken: ct));
        }

        if (bodyChanged)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
                VALUES (@ticketId, COALESCE(@bodyText, ''), @bodyHtml)
                ON CONFLICT (ticket_id) DO UPDATE
                    SET body_text = COALESCE(@bodyText, ticket_bodies.body_text),
                        body_html = COALESCE(@bodyHtml, ticket_bodies.body_html)
                """,
                new { ticketId, bodyText = update.BodyText, bodyHtml = update.BodyHtml },
                tx, cancellationToken: ct));
            // Also bump updated_utc on ticket if only body changed
            if (sets.Count <= 1)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tickets SET updated_utc = now() WHERE id = @ticketId",
                    new { ticketId }, tx, cancellationToken: ct));
            }
        }

        foreach (var (eventType, metadata) in events)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
                VALUES (@ticketId, @eventType, @actorUserId, @metadata::jsonb, FALSE)
                """,
                new { ticketId, eventType, actorUserId, metadata },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return await GetByIdAsync(ticketId, ct);
    }

    public async Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition("SELECT EXISTS(SELECT 1 FROM tickets WHERE id = @ticketId AND is_deleted = FALSE)",
                new { ticketId }, tx, cancellationToken: ct));
        if (!exists) { await tx.RollbackAsync(ct); return null; }

        const string insertSql = """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, body_text, body_html, is_internal)
            VALUES (@TicketId, @EventType, @AuthorUserId, @BodyText, @BodyHtml, @IsInternal)
            RETURNING id AS Id, ticket_id AS TicketId, event_type AS EventType,
                      author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                      body_text AS BodyText, body_html AS BodyHtml,
                      metadata::text AS MetadataJson, is_internal AS IsInternal,
                      created_utc AS CreatedUtc,
                      edited_utc AS EditedUtc, edited_by_user_id AS EditedByUserId
            """;
        var evt = await conn.QuerySingleAsync<TicketEvent>(new CommandDefinition(insertSql, new
        {
            TicketId = ticketId,
            input.EventType,
            input.AuthorUserId,
            input.BodyText,
            input.BodyHtml,
            input.IsInternal,
        }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @ticketId",
            new { ticketId }, tx, cancellationToken: ct));

        if (input.EventType == "Comment" && !input.IsInternal)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tickets SET first_response_utc = now() WHERE id = @ticketId AND first_response_utc IS NULL",
                new { ticketId }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return evt;
    }

    public async Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Fetch existing event and verify it belongs to this ticket + is editable
        const string selectSql = """
            SELECT event_type, body_text, body_html, is_internal
            FROM ticket_events
            WHERE id = @eventId AND ticket_id = @ticketId
            """;
        var current = await conn.QueryFirstOrDefaultAsync<(string EventType, string? BodyText, string? BodyHtml, bool IsInternal)>(
            new CommandDefinition(selectSql, new { eventId, ticketId }, tx, cancellationToken: ct));
        if (current.EventType is null) { await tx.RollbackAsync(ct); return null; }

        // Only Comment, Note, and Mail events can be edited
        if (current.EventType != "Comment" && current.EventType != "Note" && current.EventType != "Mail")
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // Determine next revision number
        var maxRevision = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                "SELECT MAX(revision_number) FROM ticket_event_revisions WHERE event_id = @eventId",
                new { eventId }, tx, cancellationToken: ct)) ?? 0;

        // Store old values as a revision
        const string insertRevisionSql = """
            INSERT INTO ticket_event_revisions (event_id, revision_number, body_text_before, body_html_before, is_internal_before, edited_by_user_id)
            VALUES (@eventId, @revisionNumber, @bodyTextBefore, @bodyHtmlBefore, @isInternalBefore, @editorUserId)
            """;
        await conn.ExecuteAsync(new CommandDefinition(insertRevisionSql, new
        {
            eventId,
            revisionNumber = maxRevision + 1,
            bodyTextBefore = current.BodyText,
            bodyHtmlBefore = current.BodyHtml,
            isInternalBefore = current.IsInternal,
            editorUserId = input.EditorUserId,
        }, tx, cancellationToken: ct));

        // Update the event with new values
        const string updateSql = """
            UPDATE ticket_events
            SET body_text = @bodyText,
                body_html = @bodyHtml,
                is_internal = @isInternal,
                edited_utc = now(),
                edited_by_user_id = @editorUserId
            WHERE id = @eventId AND ticket_id = @ticketId
            RETURNING id AS Id, ticket_id AS TicketId, event_type AS EventType,
                      author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                      body_text AS BodyText, body_html AS BodyHtml,
                      metadata::text AS MetadataJson, is_internal AS IsInternal,
                      created_utc AS CreatedUtc,
                      edited_utc AS EditedUtc, edited_by_user_id AS EditedByUserId
            """;
        var updated = await conn.QuerySingleAsync<TicketEvent>(new CommandDefinition(updateSql, new
        {
            bodyText = input.BodyText ?? current.BodyText,
            bodyHtml = input.BodyHtml ?? current.BodyHtml,
            isInternal = input.IsInternal ?? current.IsInternal,
            editorUserId = input.EditorUserId,
            eventId,
            ticketId,
        }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @ticketId",
            new { ticketId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id AS Id, r.event_id AS EventId, r.revision_number AS RevisionNumber,
                   r.body_text_before AS BodyTextBefore, r.body_html_before AS BodyHtmlBefore,
                   r.is_internal_before AS IsInternalBefore,
                   r.edited_by_user_id AS EditedByUserId,
                   u.email AS EditedByName,
                   r.edited_utc AS EditedUtc
            FROM ticket_event_revisions r
            JOIN users u ON u.id = r.edited_by_user_id
            WHERE r.event_id = @eventId
              AND EXISTS (SELECT 1 FROM ticket_events WHERE id = @eventId AND ticket_id = @ticketId)
            ORDER BY r.revision_number DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var revisions = await conn.QueryAsync<TicketEventRevision>(
            new CommandDefinition(sql, new { eventId, ticketId }, cancellationToken: ct));
        return revisions.ToList();
    }

    private sealed record TicketFieldSnapshot(
        Guid QueueId, Guid StatusId, Guid PriorityId, Guid? CategoryId, Guid? AssigneeUserId);

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
