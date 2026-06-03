using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

/// Hand-written Dapper queries for the ticket list/detail hot paths. Keyset
/// pagination on <c>(updated_utc DESC, id DESC)</c> lets us walk 1M rows
/// without the offset penalty. When dynamic sorting or priority float is
/// enabled, falls back to offset pagination. All filters are parameterized
/// — no string concatenation of user input reaches the SQL.
public sealed class TicketRepository : ITicketRepository, ITicketNumberLookup
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

    /// Whitelist mapping frontend field names to SQL column expressions.
    /// Prevents SQL injection via dynamic ORDER BY.
    private static readonly Dictionary<string, string> SortFieldMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["updatedUtc"]     = "t.updated_utc",
        ["createdUtc"]     = "t.created_utc",
        ["dueUtc"]         = "COALESCE(t.due_utc, '9999-12-31'::timestamptz)",
        ["priorityLevel"]  = "p.level",
        ["number"]         = "t.number",
        ["subject"]        = "t.subject",
        ["statusName"]     = "s.name",
        ["queueName"]      = "q.name",
        ["assigneeEmail"]  = "COALESCE(u.email, '')",
        ["requesterEmail"] = "c.email",
        ["companyName"]    = "COALESCE(co.name, '')",
        ["categoryName"]   = "COALESCE(cat.name, '')",
    };
    // The ticket's company is frozen at intake in t.company_id (v0.0.9 step 3).
    // RequesterCompanyId keeps its name for frontend stability — semantically
    // it is now "the ticket's resolved company id", which for the common case
    // (primary resolution) equals the requester's current primary anyway.
    private const string ListSelect = """
        SELECT
            t.id                            AS Id,
            t.number                        AS Number,
            t.subject                       AS Subject,
            t.queue_id                      AS QueueId,
            q.name                          AS QueueName,
            t.status_id                     AS StatusId,
            s.name                          AS StatusName,
            s.color                         AS StatusColor,
            s.state_category                AS StatusStateCategory,
            t.priority_id                   AS PriorityId,
            p.name                          AS PriorityName,
            p.level                         AS PriorityLevel,
            p.color                         AS PriorityColor,
            p.is_default                    AS PriorityIsDefault,
            t.requester_contact_id          AS RequesterContactId,
            c.email                         AS RequesterEmail,
            c.first_name                    AS RequesterFirstName,
            c.last_name                     AS RequesterLastName,
            t.company_id                    AS RequesterCompanyId,
            co.name                         AS CompanyName,
            t.assignee_user_id              AS AssigneeUserId,
            u.email                         AS AssigneeEmail,
            t.category_id                   AS CategoryId,
            cat.name                        AS CategoryName,
            t.created_utc                   AS CreatedUtc,
            t.updated_utc                   AS UpdatedUtc,
            t.due_utc                       AS DueUtc,
            t.awaiting_company_assignment   AS AwaitingCompanyAssignment,
            t.company_resolved_via          AS CompanyResolvedVia,
            t.ticket_type_id                AS TicketTypeId
        FROM tickets t
        JOIN queues     q ON q.id = t.queue_id
        JOIN statuses   s ON s.id = t.status_id
        JOIN priorities p ON p.id = t.priority_id
        JOIN contacts   c ON c.id = t.requester_contact_id
        LEFT JOIN companies  co  ON co.id  = t.company_id
        LEFT JOIN users      u   ON u.id   = t.assignee_user_id
        LEFT JOIN categories cat ON cat.id = t.category_id
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

        // Multi-select wins over singular: a saved view that picks "Queue A + B"
        // forwards QueueIds; ad-hoc URL params still drop in via QueueId. Same
        // pattern for status and priority. Empty list = no filter (treated as
        // null) so an empty multi-select doesn't accidentally exclude every row.
        if (query.QueueIds is { Count: > 0 }) sql.Append(" AND t.queue_id = ANY(@QueueIds)");
        else if (query.QueueId.HasValue) sql.Append(" AND t.queue_id = @QueueId");
        if (query.StatusIds is { Count: > 0 }) sql.Append(" AND t.status_id = ANY(@StatusIds)");
        else if (query.StatusId.HasValue) sql.Append(" AND t.status_id = @StatusId");
        if (query.PriorityIds is { Count: > 0 }) sql.Append(" AND t.priority_id = ANY(@PriorityIds)");
        else if (query.PriorityId.HasValue) sql.Append(" AND t.priority_id = @PriorityId");
        if (query.AssigneeUserId.HasValue) sql.Append(" AND t.assignee_user_id = @AssigneeUserId");
        if (query.RequesterContactId.HasValue) sql.Append(" AND t.requester_contact_id = @RequesterContactId");
        if (query.RequesterCompanyId.HasValue) sql.Append(" AND t.company_id = @RequesterCompanyId");
        if (query.OpenOnly) sql.Append(" AND s.state_category NOT IN ('Resolved','Closed')");

        // Queue-access enforcement: restrict to only the queues the caller
        // is allowed to see. When null (admin), no filter is applied.
        if (query.AccessibleQueueIds is not null)
            sql.Append(" AND t.queue_id = ANY(@AccessibleQueueIds)");

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Subject (tickets.search_vector) OR exact ticket number OR body
            // (ticket_event_search.search_vector — sidecar fed by trigger).
            // The body-EXISTS rides the GIN index on tes.search_vector so it
            // stays fast even on large datasets.
            sql.Append(" AND (");
            sql.Append("t.search_vector @@ plainto_tsquery('simple', @Search)");
            sql.Append(" OR t.number::text = @SearchRaw");
            sql.Append(" OR EXISTS (SELECT 1 FROM ticket_event_search tes");
            sql.Append(" WHERE tes.ticket_id = t.id AND tes.search_vector @@ plainto_tsquery('simple', @Search))");
            sql.Append(")");
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
                // Customer-portal visibility is bound to the ticket's frozen
                // company (t.company_id), not the requester's current primary.
                // That way moving a contact between companies doesn't leak old
                // tickets into the new company's portal view.
                sql.Append(" AND t.company_id = @ViewerCompanyId");
                break;
            case VisibilityScope.All:
            default:
                break;
        }

        // Determine if we need offset-based pagination (dynamic sort, priority
        // float, or open-first bucket sort — the latter can't ride the
        // (updated_utc, id) keyset cursor since the bucket key splits the order).
        var hasDynamicSort = query.SortField is not null
            && !string.Equals(query.SortField, "updatedUtc", StringComparison.OrdinalIgnoreCase);
        var useOffset = hasDynamicSort || query.PriorityFloat || query.OpenFirst;

        if (useOffset)
        {
            // Offset pagination: no keyset cursor needed.
        }
        else
        {
            // Keyset cursor: rows strictly older than the cursor tuple.
            if (query.CursorUpdatedUtc.HasValue && query.CursorId.HasValue)
            {
                sql.Append(" AND (t.updated_utc, t.id) < (@CursorUpdatedUtc, @CursorId)");
            }
        }

        // Build ORDER BY. Open-first prepends a bucket key so resolved/closed
        // tickets always sort below open ones, regardless of secondary sort.
        var orderClauses = new List<string>();
        if (query.OpenFirst)
            orderClauses.Add("(CASE WHEN s.state_category IN ('Resolved','Closed') THEN 1 ELSE 0 END) ASC");
        if (query.PriorityFloat)
        {
            orderClauses.Add("(CASE WHEN p.is_default THEN 1 ELSE 0 END)");
            orderClauses.Add("CASE WHEN NOT p.is_default THEN p.level END ASC");
        }
        if (query.SortField is not null && SortFieldMap.TryGetValue(query.SortField, out var sortColumn))
        {
            var dir = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            orderClauses.Add($"{sortColumn} {dir}");
        }
        else
        {
            orderClauses.Add("t.updated_utc DESC");
        }
        orderClauses.Add("t.id DESC");
        sql.Append(" ORDER BY ").Append(string.Join(", ", orderClauses));

        // Hard safety ceiling. The list/views load in a single page (no lazy
        // loading) up to the admin-configurable Tickets.ListPageSize (default
        // 1000); this clamp is the absolute backstop so a hand-crafted
        // ?limit=999999 can never ask Postgres for an unbounded result set.
        var limit = Math.Clamp(query.Limit, 1, 5000);
        sql.Append(" LIMIT @Limit");
        if (useOffset)
            sql.Append(" OFFSET @Offset");

        var parameters = new
        {
            query.QueueId,
            query.StatusId,
            query.PriorityId,
            query.AssigneeUserId,
            query.RequesterContactId,
            query.RequesterCompanyId,
            Search = query.Search ?? "",
            SearchRaw = query.Search ?? "",
            ViewerContactId = viewerUserId ?? Guid.Empty,
            ViewerCompanyId = viewerCompanyId ?? Guid.Empty,
            query.CursorUpdatedUtc,
            query.CursorId,
            Limit = limit,
            Offset = query.Offset ?? 0,
            AccessibleQueueIds = query.AccessibleQueueIds as IEnumerable<Guid>,
            QueueIds = query.QueueIds as IEnumerable<Guid>,
            StatusIds = query.StatusIds as IEnumerable<Guid>,
            PriorityIds = query.PriorityIds as IEnumerable<Guid>,
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TicketListItem>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        if (useOffset)
        {
            int? nextOffset = rows.Count == limit
                ? (query.Offset ?? 0) + rows.Count
                : null;
            return new TicketPage(rows, null, null, nextOffset);
        }
        else
        {
            DateTime? nextUpdated = null;
            Guid? nextId = null;
            if (rows.Count == limit && rows.Count > 0)
            {
                var last = rows[^1];
                nextUpdated = last.UpdatedUtc;
                nextId = last.Id;
            }
            return new TicketPage(rows, nextUpdated, nextId);
        }
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
                   closed_utc AS ClosedUtc, is_deleted AS IsDeleted,
                   company_id AS CompanyId,
                   awaiting_company_assignment AS AwaitingCompanyAssignment,
                   company_resolved_via AS CompanyResolvedVia,
                   merged_into_ticket_id AS MergedIntoTicketId,
                   merged_utc AS MergedUtc,
                   merged_by_user_id AS MergedByUserId,
                   split_from_ticket_id AS SplitFromTicketId,
                   split_from_utc AS SplitFromUtc,
                   split_from_user_id AS SplitFromUserId,
                   pending_till_utc AS PendingTillUtc,
                   pending_till_next_trigger_id AS PendingTillNextTriggerId,
                   parent_ticket_id AS ParentTicketId,
                   parent_linked_utc AS ParentLinkedUtc,
                   parent_linked_by_user_id AS ParentLinkedByUserId,
                   ticket_type_id AS TicketTypeId,
                   zammad_ticket_id AS ZammadTicketId,
                   zammad_ticket_number AS ZammadTicketNumber
            FROM tickets WHERE id = @id AND is_deleted = FALSE
            """;
        const string bodySql = """
            SELECT ticket_id AS TicketId, body_text AS BodyText, body_html AS BodyHtml
            FROM ticket_bodies WHERE ticket_id = @id
            """;
        const string eventsSql = """
            SELECT e.id AS Id, e.ticket_id AS TicketId, e.event_type AS EventType,
                   e.author_user_id AS AuthorUserId, e.author_contact_id AS AuthorContactId,
                   COALESCE(au.email, NULLIF(CONCAT_WS(' ', ac.first_name, ac.last_name), ''), e.metadata->>'authorName') AS AuthorName,
                   e.body_text AS BodyText, e.body_html AS BodyHtml,
                   e.metadata::text AS MetadataJson, e.is_internal AS IsInternal,
                   e.created_utc AS CreatedUtc,
                   e.edited_utc AS EditedUtc, e.edited_by_user_id AS EditedByUserId
            FROM ticket_events e
            LEFT JOIN users    au ON au.id = e.author_user_id
            LEFT JOIN contacts ac ON ac.id = e.author_contact_id
            WHERE e.ticket_id = @id ORDER BY e.created_utc, e.id
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

        const string pinsSql = """
            SELECT p.id AS Id, p.event_id AS EventId, p.ticket_id AS TicketId,
                   p.pinned_by_user_id AS PinnedByUserId,
                   u.email AS PinnedByName,
                   p.remark AS Remark,
                   p.created_utc AS CreatedUtc
            FROM ticket_event_pins p
            JOIN users u ON u.id = p.pinned_by_user_id
            WHERE p.ticket_id = @id
            ORDER BY p.created_utc
            """;
        var pins = (await conn.QueryAsync<TicketEventPin>(
            new CommandDefinition(pinsSql, new { id }, cancellationToken: ct))).ToList();

        return new TicketDetail(ticket, body, events, pins);
    }

    public async Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct)
    {
        // v0.0.39 — ticket_type_id is NOT NULL. The COALESCE-with-subquery
        // pattern lets the caller pass an explicit id (chosen by the agent
        // via a manual trigger) or fall through to the 'support' system
        // type when the caller hasn't picked one. Resolving inline avoids
        // an extra round-trip and keeps the create flow a single statement.
        const string insertTicket = """
            INSERT INTO tickets (subject, requester_contact_id, assignee_user_id, queue_id,
                                 status_id, priority_id, category_id, source,
                                 company_id, awaiting_company_assignment, company_resolved_via,
                                 pending_till_utc, ticket_type_id)
            VALUES (@Subject, @RequesterContactId, @AssigneeUserId, @QueueId,
                    @StatusId, @PriorityId, @CategoryId, @Source,
                    @CompanyId, @AwaitingCompanyAssignment, @CompanyResolvedVia,
                    @PendingTillUtc,
                    COALESCE(@TicketTypeId,
                             (SELECT id FROM ticket_types WHERE code = 'support' LIMIT 1)))
            RETURNING id AS Id, number AS Number, subject AS Subject,
                      requester_contact_id AS RequesterContactId, assignee_user_id AS AssigneeUserId,
                      queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
                      category_id AS CategoryId, source AS Source, external_ref AS ExternalRef,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, due_utc AS DueUtc,
                      first_response_utc AS FirstResponseUtc, resolved_utc AS ResolvedUtc,
                      closed_utc AS ClosedUtc, is_deleted AS IsDeleted,
                      company_id AS CompanyId,
                      awaiting_company_assignment AS AwaitingCompanyAssignment,
                      company_resolved_via AS CompanyResolvedVia,
                      merged_into_ticket_id AS MergedIntoTicketId,
                      merged_utc AS MergedUtc,
                      merged_by_user_id AS MergedByUserId,
                      split_from_ticket_id AS SplitFromTicketId,
                      split_from_utc AS SplitFromUtc,
                      split_from_user_id AS SplitFromUserId,
                      pending_till_utc AS PendingTillUtc,
                      pending_till_next_trigger_id AS PendingTillNextTriggerId,
                      parent_ticket_id AS ParentTicketId,
                      parent_linked_utc AS ParentLinkedUtc,
                      parent_linked_by_user_id AS ParentLinkedByUserId,
                      ticket_type_id AS TicketTypeId,
                      zammad_ticket_id AS ZammadTicketId,
                      zammad_ticket_number AS ZammadTicketNumber
            """;
        const string insertBody = """
            INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
            VALUES (@TicketId, @BodyText, @BodyHtml)
            """;
        const string insertEvent = """
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id, body_text, body_html, metadata)
            VALUES (@TicketId, 'Created', @AuthorContactId, NULL, NULL, COALESCE(@MetadataJson::jsonb, '{}'::jsonb))
            """;
        // v0.0.39 — optional first-note event for the manual "create
        // linked X ticket" flow. Inserted in the same transaction as
        // the ticket itself so the timeline is consistent at first
        // GET. is_internal honours the trigger config; the body is
        // pre-rendered HTML supplied by the caller.
        const string insertInitialNote = """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, body_html, is_internal, metadata)
            VALUES (@TicketId, 'Note', @AuthorUserId, @BodyHtml, @IsInternal, '{}'::jsonb)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ticket = await conn.QuerySingleAsync<Ticket>(new CommandDefinition(insertTicket, input, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertBody,
            new { TicketId = ticket.Id, input.BodyText, input.BodyHtml }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertEvent,
            new
            {
                TicketId = ticket.Id,
                AuthorContactId = input.RequesterContactId,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { source = input.Source }),
            },
            tx, cancellationToken: ct));

        if (input.InitialNote is { } note && !string.IsNullOrWhiteSpace(note.BodyHtml))
        {
            await conn.ExecuteAsync(new CommandDefinition(insertInitialNote,
                new
                {
                    TicketId = ticket.Id,
                    AuthorUserId = (Guid?)null,
                    BodyHtml = note.BodyHtml,
                    IsInternal = note.IsInternal,
                },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return ticket;
    }

    public async Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string readSql = """
            SELECT queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
                   category_id AS CategoryId, assignee_user_id AS AssigneeUserId,
                   pending_till_utc AS PendingTillUtc
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketFieldSnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) { await tx.RollbackAsync(ct); return null; }

        // v0.0.40 — auto-flip status when a queue change would land the
        // ticket on a status outside the new queue's allowed-list. The
        // status-dropdown filters in the UI prevent the common case at
        // save-time, but a trigger that only does set_queue or a hand-
        // rolled API call still needs the runtime safety net. We rebind
        // `update` (record copy via `with`) so every downstream branch
        // — change-event metadata, state-category side-effects, SQL
        // parameter binding — picks up the auto-flip without further
        // wiring.
        if (update.QueueId.HasValue && update.QueueId != current.QueueId)
        {
            var scope = await conn.QueryFirstOrDefaultAsync<QueueStatusScopeRow>(
                new CommandDefinition(
                    "SELECT allowed_status_ids AS AllowedStatusIds, default_status_id AS DefaultStatusId FROM queues WHERE id = @id",
                    new { id = update.QueueId.Value }, tx, cancellationToken: ct));
            if (scope is not null
                && scope.AllowedStatusIds is { Length: > 0 }
                && scope.DefaultStatusId.HasValue)
            {
                var effective = update.StatusId ?? current.StatusId;
                if (!scope.AllowedStatusIds.Contains(effective))
                {
                    update = update with { StatusId = scope.DefaultStatusId };
                }
            }
        }

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
            var fromCategory = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT state_category FROM statuses WHERE id = @id",
                new { id = current.StatusId }, tx, cancellationToken: ct));
            var toCategory = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT state_category FROM statuses WHERE id = @id",
                new { id = update.StatusId.Value }, tx, cancellationToken: ct));
            events.Add(("StatusChange", System.Text.Json.JsonSerializer.Serialize(
                new { from = current.StatusId, to = update.StatusId, fromName, toName, fromCategory, toCategory })));
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

        // v0.0.37 — gate pending-till writes against the *effective*
        // status (after a same-PATCH flip). The auto-clear below
        // already wipes the column when flipping out of Pending, so
        // any value the caller sends in that case is ignored. Has to
        // run BEFORE the no-op short-circuit further down, otherwise a
        // pending-till-only PATCH (the common agent-edit case) rolls
        // back with sets.Count == 0.
        bool writePendingTill =
            update.PendingTillUtc.HasValue
            && (current.StatusId == update.StatusId
                || !update.StatusId.HasValue
                || await IsPendingStatusAsync(conn, tx, update.StatusId!.Value, ct));
        if (writePendingTill) sets.Add("pending_till_utc = @NewPendingTillUtc");

        if (sets.Count == 0 && !bodyChanged) { await tx.RollbackAsync(ct); return await GetByIdAsync(ticketId, ct); }

        sets.Add("updated_utc = now()");

        if (update.StatusId.HasValue && update.StatusId != current.StatusId)
        {
            var stateCategory = await conn.ExecuteScalarAsync<string>(
                new CommandDefinition("SELECT state_category FROM statuses WHERE id = @id",
                    new { id = update.StatusId }, tx, cancellationToken: ct));
            if (stateCategory == "Resolved") sets.Add("resolved_utc = COALESCE(resolved_utc, now())");
            if (stateCategory == "Closed") sets.Add("closed_utc = COALESCE(closed_utc, now())");
            // v0.0.37 — flipping OUT of Pending wipes the reminder.
            // The trigger-driven `next_trigger_id` chain (v0.0.24) is
            // tied to the same lifecycle so we clear it in the same
            // breath. Flipping IN to Pending without an explicit value
            // is the endpoint's responsibility — the repo only persists
            // what it's given.
            if (stateCategory != "Pending")
            {
                sets.Add("pending_till_utc = NULL");
                sets.Add("pending_till_next_trigger_id = NULL");
            }
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
                NewPendingTillUtc = update.PendingTillUtc,
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

    public async Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the ticket row and grab the previous company_id so the
        // timeline event can render "Acme → Widgets" rather than just "to Widgets".
        const string readSql = """
            SELECT id AS TicketId, company_id AS CompanyId
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketCompanySnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) { await tx.RollbackAsync(ct); return null; }

        const string nameSql = "SELECT name FROM companies WHERE id = @id";
        var fromName = current.CompanyId.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(nameSql, new { id = current.CompanyId.Value }, tx, cancellationToken: ct))
            : null;
        var toName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(nameSql, new { id = companyId }, tx, cancellationToken: ct));
        if (toName is null) { await tx.RollbackAsync(ct); return null; }

        const string updateSql = """
            UPDATE tickets
               SET company_id = @companyId,
                   awaiting_company_assignment = FALSE,
                   company_resolved_via = 'manual',
                   updated_utc = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId, companyId }, tx, cancellationToken: ct));
        if (rows == 0) { await tx.RollbackAsync(ct); return null; }

        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            from = current.CompanyId,
            to = companyId,
            fromName,
            toName,
            resolvedVia = "manual",
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'CompanyAssignment', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return await GetByIdAsync(ticketId, ct);
    }

    private sealed record TicketCompanySnapshot(Guid TicketId, Guid? CompanyId);

    public async Task<TicketDetail?> ChangeRequesterAsync(
        Guid ticketId,
        Guid newContactId,
        Guid? newCompanyId,
        bool awaitingCompanyAssignment,
        string? companyResolvedVia,
        Guid actorUserId,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string readSql = """
            SELECT id AS TicketId, requester_contact_id AS RequesterContactId, company_id AS CompanyId
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketRequesterSnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) { await tx.RollbackAsync(ct); return null; }

        // No-op: same contact — don't touch anything so updated_utc stays put
        // and no noise event lands on the timeline.
        if (current.RequesterContactId == newContactId
            && current.CompanyId == newCompanyId)
        {
            await tx.RollbackAsync(ct);
            return await GetByIdAsync(ticketId, ct);
        }

        const string contactNameSql = """
            SELECT CONCAT_WS(' ', first_name, last_name) AS Name, email AS Email
            FROM contacts WHERE id = @id
            """;
        var fromContact = await conn.QueryFirstOrDefaultAsync<ContactNameRow>(
            new CommandDefinition(contactNameSql, new { id = current.RequesterContactId }, tx, cancellationToken: ct));
        var toContact = await conn.QueryFirstOrDefaultAsync<ContactNameRow>(
            new CommandDefinition(contactNameSql, new { id = newContactId }, tx, cancellationToken: ct));
        if (toContact is null) { await tx.RollbackAsync(ct); return null; }

        const string companyNameSql = "SELECT name FROM companies WHERE id = @id";
        var fromCompanyName = current.CompanyId.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                companyNameSql, new { id = current.CompanyId.Value }, tx, cancellationToken: ct))
            : null;
        var toCompanyName = newCompanyId.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                companyNameSql, new { id = newCompanyId.Value }, tx, cancellationToken: ct))
            : null;

        const string updateSql = """
            UPDATE tickets
               SET requester_contact_id = @newContactId,
                   company_id = @newCompanyId,
                   awaiting_company_assignment = @awaiting,
                   company_resolved_via = @resolvedVia,
                   updated_utc = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            ticketId,
            newContactId,
            newCompanyId,
            awaiting = awaitingCompanyAssignment,
            resolvedVia = companyResolvedVia,
        }, tx, cancellationToken: ct));
        if (rows == 0) { await tx.RollbackAsync(ct); return null; }

        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            fromContactId = current.RequesterContactId,
            toContactId = newContactId,
            fromName = fromContact?.Name,
            toName = toContact.Name,
            fromEmail = fromContact?.Email,
            toEmail = toContact.Email,
            fromCompanyId = current.CompanyId,
            toCompanyId = newCompanyId,
            fromCompanyName,
            toCompanyName,
            resolvedVia = companyResolvedVia,
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'RequesterChange', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return await GetByIdAsync(ticketId, ct);
    }

    private sealed record TicketRequesterSnapshot(Guid TicketId, Guid RequesterContactId, Guid? CompanyId);
    private sealed record ContactNameRow(string? Name, string? Email);

    public async Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition("SELECT EXISTS(SELECT 1 FROM tickets WHERE id = @ticketId AND is_deleted = FALSE)",
                new { ticketId }, tx, cancellationToken: ct));
        if (!exists) { await tx.RollbackAsync(ct); return null; }

        const string insertSql = """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, author_contact_id,
                                        body_text, body_html, is_internal, metadata)
            VALUES (@TicketId, @EventType, @AuthorUserId, @AuthorContactId,
                    @BodyText, @BodyHtml, @IsInternal, COALESCE(@MetadataJson::jsonb, '{}'::jsonb))
            RETURNING id AS Id, ticket_id AS TicketId, event_type AS EventType,
                      author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                      COALESCE(
                          (SELECT email FROM users WHERE id = author_user_id),
                          (SELECT CONCAT_WS(' ', first_name, last_name) FROM contacts WHERE id = author_contact_id)
                      ) AS AuthorName,
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
            input.AuthorContactId,
            input.BodyText,
            input.BodyHtml,
            input.IsInternal,
            input.MetadataJson,
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

        // v0.0.51 — keep event_type in sync with is_internal for the
        // Note/Comment pair. The timeline + PDF + customer-portal
        // filters render labels and visibility from event_type (Note =
        // "Internal note", Comment = "Reply"), so toggling is_internal
        // without flipping the type leaves a hybrid row that *looks*
        // unchanged in the UI even though the DB write succeeded. Mail
        // events keep their own type — flipping a sent mail to Note
        // would lose its provenance and the OutboundMailService /
        // MailIngestService rely on event_type='Mail' downstream.
        // RepostAsPublicReplyHandler already enforces the same pairing
        // when it duplicates an internal note as a public reply (see
        // its comment), so this brings the agent-edit path in line.
        var resolvedIsInternal = input.IsInternal ?? current.IsInternal;
        var resolvedEventType = current.EventType is "Note" or "Comment"
            ? (resolvedIsInternal ? "Note" : "Comment")
            : current.EventType;

        const string updateSql = """
            UPDATE ticket_events
            SET body_text = @bodyText,
                body_html = @bodyHtml,
                is_internal = @isInternal,
                event_type = @eventType,
                edited_utc = now(),
                edited_by_user_id = @editorUserId
            WHERE id = @eventId AND ticket_id = @ticketId
            RETURNING id AS Id, ticket_id AS TicketId, event_type AS EventType,
                      author_user_id AS AuthorUserId, author_contact_id AS AuthorContactId,
                      COALESCE(
                          (SELECT email FROM users WHERE id = author_user_id),
                          (SELECT CONCAT_WS(' ', first_name, last_name) FROM contacts WHERE id = author_contact_id)
                      ) AS AuthorName,
                      body_text AS BodyText, body_html AS BodyHtml,
                      metadata::text AS MetadataJson, is_internal AS IsInternal,
                      created_utc AS CreatedUtc,
                      edited_utc AS EditedUtc, edited_by_user_id AS EditedByUserId
            """;
        var updated = await conn.QuerySingleAsync<TicketEvent>(new CommandDefinition(updateSql, new
        {
            bodyText = input.BodyText ?? current.BodyText,
            bodyHtml = input.BodyHtml ?? current.BodyHtml,
            isInternal = resolvedIsInternal,
            eventType = resolvedEventType,
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

    public async Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO ticket_event_pins (event_id, ticket_id, pinned_by_user_id, remark)
            SELECT @eventId, @ticketId, @userId, @remark
            WHERE EXISTS (
                SELECT 1 FROM ticket_events
                WHERE id = @eventId AND ticket_id = @ticketId
                  AND event_type IN ('Comment','Note','Mail','MailReceived','MailSent','IntakeFormSubmitted')
            )
            ON CONFLICT (event_id) DO NOTHING
            RETURNING id AS Id, event_id AS EventId, ticket_id AS TicketId,
                      pinned_by_user_id AS PinnedByUserId,
                      (SELECT email FROM users WHERE id = pinned_by_user_id) AS PinnedByName,
                      remark AS Remark, created_utc AS CreatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var pin = await conn.QueryFirstOrDefaultAsync<TicketEventPin>(
            new CommandDefinition(sql, new { eventId, ticketId, userId, remark = remark ?? "" }, cancellationToken: ct));

        // If ON CONFLICT hit (already pinned), return the existing pin
        if (pin is null)
        {
            const string existingSql = """
                SELECT p.id AS Id, p.event_id AS EventId, p.ticket_id AS TicketId,
                       p.pinned_by_user_id AS PinnedByUserId,
                       u.email AS PinnedByName,
                       p.remark AS Remark, p.created_utc AS CreatedUtc
                FROM ticket_event_pins p
                JOIN users u ON u.id = p.pinned_by_user_id
                WHERE p.event_id = @eventId AND p.ticket_id = @ticketId
                """;
            pin = await conn.QueryFirstOrDefaultAsync<TicketEventPin>(
                new CommandDefinition(existingSql, new { eventId, ticketId }, cancellationToken: ct));
        }

        return pin;
    }

    public async Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = "DELETE FROM ticket_event_pins WHERE event_id = @eventId AND ticket_id = @ticketId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { eventId, ticketId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct)
    {
        const string sql = """
            UPDATE ticket_event_pins SET remark = @remark
            WHERE event_id = @eventId AND ticket_id = @ticketId
            RETURNING id AS Id, event_id AS EventId, ticket_id AS TicketId,
                      pinned_by_user_id AS PinnedByUserId,
                      (SELECT email FROM users WHERE id = pinned_by_user_id) AS PinnedByName,
                      remark AS Remark, created_utc AS CreatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketEventPin>(
            new CommandDefinition(sql, new { eventId, ticketId, remark = remark ?? "" }, cancellationToken: ct));
    }

    private sealed record TicketFieldSnapshot(
        Guid QueueId, Guid StatusId, Guid PriorityId, Guid? CategoryId, Guid? AssigneeUserId,
        DateTime? PendingTillUtc);

    // v0.0.40 — projection used by the queue auto-flip path in
    // UpdateFieldsAsync. Sealed class (not record) per the project's
    // dapper_record_struct_null_bug convention; default_status_id is
    // nullable in the schema and may legitimately be null.
    private sealed class QueueStatusScopeRow
    {
        public Guid[] AllowedStatusIds { get; set; } = Array.Empty<Guid>();
        public Guid? DefaultStatusId { get; set; }
    }

    /// Returns true when the supplied status_id sits in the
    /// <c>Pending</c> state_category. Used by UpdateFieldsAsync to
    /// decide whether to honour an explicit pending_till_utc write
    /// alongside a status flip — agents on a non-Pending status can't
    /// poke a stray pending-till value into the row through a stale
    /// form.
    private static async Task<bool> IsPendingStatusAsync(
        Npgsql.NpgsqlConnection conn, System.Data.Common.DbTransaction tx,
        Guid statusId, CancellationToken ct)
    {
        var cat = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state_category FROM statuses WHERE id = @id",
            new { id = statusId }, tx, cancellationToken: ct));
        return cat == "Pending";
    }

    public async Task<bool> EventBelongsToTicketAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM ticket_events WHERE id = @eventId AND ticket_id = @ticketId)";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { ticketId, eventId }, cancellationToken: ct));
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

    public Task<Guid?> GetMergedIntoAsync(Guid ticketId, CancellationToken ct)
    {
        // Tiny lookup used by the mail-ingest resolver to follow a redirect
        // chain hop-by-hop. Soft-deleted tickets still expose the pointer so a
        // mid-flight delete doesn't strand the redirect.
        const string sql = "SELECT merged_into_ticket_id FROM tickets WHERE id = @ticketId";
        return GetMergedIntoCoreAsync(sql, ticketId, ct);
    }

    private async Task<Guid?> GetMergedIntoCoreAsync(string sql, Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(
        string? search,
        Guid excludeTicketId,
        IReadOnlyCollection<Guid>? accessibleQueueIds,
        int limit,
        CancellationToken ct)
    {
        // Picker filters out tickets that are already merged so an agent can't
        // accidentally select a "tombstone" as the target. We exclude self
        // (no-op merge) and respect queue-access — admins pass null and skip
        // the filter. Limit is clamped to a sane range to keep typeahead snappy.
        var clampedLimit = Math.Clamp(limit, 1, 50);
        var sql = new StringBuilder("""
            SELECT t.id            AS Id,
                   t.number        AS Number,
                   t.subject       AS Subject,
                   t.status_id     AS StatusId,
                   s.name          AS StatusName,
                   s.color         AS StatusColor,
                   s.state_category AS StatusStateCategory,
                   t.company_id    AS CompanyId,
                   co.name         AS CompanyName,
                   t.requester_contact_id AS RequesterContactId,
                   c.email         AS RequesterEmail,
                   c.first_name    AS RequesterFirstName,
                   c.last_name     AS RequesterLastName
            FROM tickets t
            JOIN statuses s ON s.id = t.status_id
            JOIN contacts c ON c.id = t.requester_contact_id
            LEFT JOIN companies co ON co.id = t.company_id
            WHERE t.is_deleted = FALSE
              AND t.merged_into_ticket_id IS NULL
              AND t.id <> @excludeTicketId
            """);

        if (accessibleQueueIds is not null)
            sql.Append(" AND t.queue_id = ANY(@AccessibleQueueIds)");

        if (!string.IsNullOrWhiteSpace(search))
        {
            // FTS on subject OR exact ticket-number match — same shape as the
            // list endpoint so an agent can paste a number and find it.
            sql.Append(" AND (t.search_vector @@ plainto_tsquery('simple', @Search) OR t.number::text = @SearchRaw)");
        }

        sql.Append(" ORDER BY t.updated_utc DESC, t.id DESC LIMIT @Limit");

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TicketPickerHit>(new CommandDefinition(sql.ToString(), new
        {
            excludeTicketId,
            AccessibleQueueIds = accessibleQueueIds as IEnumerable<Guid>,
            Search = search ?? string.Empty,
            SearchRaw = search ?? string.Empty,
            Limit = clampedLimit,
        }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<long>> GetMergedSourceTicketNumbersAsync(Guid targetTicketId, CancellationToken ct)
    {
        // Sparse index ix_tickets_merged_into makes this an index-only scan even
        // at scale. Order by merged_utc ASC so the "Merged from #A, #B" strip
        // reads chronologically.
        const string sql = """
            SELECT number FROM tickets
            WHERE merged_into_ticket_id = @targetTicketId
            ORDER BY merged_utc NULLS LAST, number
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<long>(
            new CommandDefinition(sql, new { targetTicketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<MergeResult?> MergeAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        Guid actorUserId,
        bool acknowledgedCrossCustomer,
        CancellationToken ct)
    {
        if (sourceTicketId == targetTicketId)
            return new MergeResult(false, 0, 0, 0, false, MergeFailureReason.SameTicket);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock both rows up-front (deterministic order to avoid deadlocks with
        // a parallel A↔B merge). is_deleted=FALSE filter keeps soft-deleted
        // tickets out of the merge entirely.
        var orderedIds = sourceTicketId.CompareTo(targetTicketId) < 0
            ? new[] { sourceTicketId, targetTicketId }
            : new[] { targetTicketId, sourceTicketId };
        const string lockSql = """
            SELECT id AS Id, number AS Number, requester_contact_id AS RequesterContactId,
                   company_id AS CompanyId, merged_into_ticket_id AS MergedIntoTicketId
            FROM tickets WHERE id = @id AND is_deleted = FALSE
            FOR UPDATE
            """;
        var locked = new Dictionary<Guid, MergeLockedRow>();
        foreach (var id in orderedIds)
        {
            var row = await conn.QueryFirstOrDefaultAsync<MergeLockedRow>(
                new CommandDefinition(lockSql, new { id }, tx, cancellationToken: ct));
            if (row is not null) locked[id] = row;
        }

        if (!locked.TryGetValue(sourceTicketId, out var source))
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, 0, 0, false, MergeFailureReason.SourceNotFound);
        }
        if (!locked.TryGetValue(targetTicketId, out var target))
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, 0, false, MergeFailureReason.TargetNotFound);
        }
        if (source.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, target.Number, false, MergeFailureReason.AlreadyMerged);
        }
        if (target.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, target.Number, false, MergeFailureReason.AlreadyMerged);
        }

        // Cycle check: walk the target's existing merge chain (target itself is
        // not merged, so this is normally a no-op; defensive against future
        // multi-hop scenarios).
        var hop = target.MergedIntoTicketId;
        var hops = 0;
        while (hop is not null && hops++ < 16)
        {
            if (hop == sourceTicketId)
            {
                await tx.RollbackAsync(ct);
                return new MergeResult(false, 0, source.Number, target.Number, false, MergeFailureReason.WouldCycle);
            }
            hop = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT merged_into_ticket_id FROM tickets WHERE id = @id",
                new { id = hop }, tx, cancellationToken: ct));
        }

        var crossCustomer = source.RequesterContactId != target.RequesterContactId
            || source.CompanyId != target.CompanyId;
        if (crossCustomer && !acknowledgedCrossCustomer)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult(false, 0, source.Number, target.Number, true, MergeFailureReason.CrossCustomerNotAcknowledged);
        }

        // Merged status row is seeded by TaxonomySeeder with slug='merged'.
        // We resolve by slug rather than caching the id so admins can re-color
        // or rename without a config change.
        var mergedStatusId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM statuses WHERE slug = 'merged' LIMIT 1",
            transaction: tx, cancellationToken: ct));
        if (mergedStatusId is null)
        {
            // Seeder hasn't run or row was force-deleted. Insert it inline so
            // the merge can complete; idempotent with the seeder's ON CONFLICT.
            mergedStatusId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO statuses (name, slug, state_category, color, icon, sort_order, is_active, is_system, is_default)
                VALUES ('Merged', 'merged', 'Closed', '#a855f7', 'git-merge', 60, TRUE, TRUE, FALSE)
                ON CONFLICT (slug) DO UPDATE SET updated_utc = now()
                RETURNING id
                """, transaction: tx, cancellationToken: ct));
        }

        // Re-point everything that hangs off the source ticket to the target.
        // ticket_events is the heaviest — we tag every moved event in metadata
        // so the timeline can render a "from #1234" badge.
        var moved = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_events
               SET ticket_id = @target,
                   metadata = jsonb_set(
                       jsonb_set(metadata, '{mergedFromTicketId}', to_jsonb(@source::text), TRUE),
                       '{mergedFromTicketNumber}', to_jsonb(@sourceNumber), TRUE)
             WHERE ticket_id = @source
            """, new { source = sourceTicketId, target = targetTicketId, sourceNumber = source.Number },
            tx, cancellationToken: ct));

        // The ticket_event_search trigger only fires on INSERT or on UPDATE OF
        // body_text/body_html — not on UPDATE OF ticket_id. We therefore
        // re-point the sidecar explicitly so search keeps returning hits for
        // moved events under the surviving ticket.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_event_search SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE mail_messages SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_event_pins SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_notifications SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE intake_form_instances SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        // Re-point logged time. Without this, agents who already booked
        // hours on the soon-to-be-merged source would either lose the
        // attribution entirely or be left chasing a tombstone ticket.
        // The count is included in the target's system-note metadata so
        // the audit story stays self-contained.
        var movedTimesheetEntries = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE timesheet_entries SET ticket_id = @target WHERE ticket_id = @source",
            new { source = sourceTicketId, target = targetTicketId }, tx, cancellationToken: ct));

        // The original ticket-body of the source is a 1:1 row, not part of the
        // event stream. To preserve the requester's first message we project
        // it onto the target as a Comment event timestamped at the source's
        // creation. Then we replace the source body with a placeholder so the
        // merged ticket reads cleanly.
        var sourceBody = await conn.QueryFirstOrDefaultAsync<(string BodyText, string? BodyHtml)>(
            new CommandDefinition(
                "SELECT body_text AS BodyText, body_html AS BodyHtml FROM ticket_bodies WHERE ticket_id = @source",
                new { source = sourceTicketId }, tx, cancellationToken: ct));
        var sourceCreatedUtc = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT created_utc FROM tickets WHERE id = @source",
            new { source = sourceTicketId }, tx, cancellationToken: ct));
        var sourceRequesterId = source.RequesterContactId;
        if (!string.IsNullOrWhiteSpace(sourceBody.BodyText) || !string.IsNullOrWhiteSpace(sourceBody.BodyHtml))
        {
            var bodyMetadata = JsonSerializer.Serialize(new
            {
                mergedFromTicketId = sourceTicketId.ToString(),
                mergedFromTicketNumber = source.Number,
                isOriginalDescription = true,
            });
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ticket_events (ticket_id, event_type, author_contact_id,
                                           body_text, body_html, metadata, is_internal, created_utc)
                VALUES (@target, 'Comment', @authorContactId, @bodyText, @bodyHtml,
                        @metadata::jsonb, FALSE, @createdUtc)
                """, new
            {
                target = targetTicketId,
                authorContactId = sourceRequesterId,
                bodyText = sourceBody.BodyText ?? string.Empty,
                bodyHtml = sourceBody.BodyHtml,
                metadata = bodyMetadata,
                createdUtc = sourceCreatedUtc,
            }, tx, cancellationToken: ct));
            moved += 1;
        }

        var targetNumber = target.Number;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_bodies
               SET body_text = @placeholder, body_html = NULL
             WHERE ticket_id = @source
            """, new
        {
            source = sourceTicketId,
            placeholder = $"This ticket was merged into #{targetNumber}.",
        }, tx, cancellationToken: ct));

        // Stamp the source ticket: status flip + merge pointer + closed_utc so
        // SLA-state and reporting queries treat it as a finalised terminal row.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tickets
               SET status_id            = @mergedStatusId,
                   merged_into_ticket_id = @target,
                   merged_utc            = now(),
                   merged_by_user_id     = @actorUserId,
                   resolved_utc          = COALESCE(resolved_utc, now()),
                   closed_utc            = COALESCE(closed_utc, now()),
                   updated_utc           = now()
             WHERE id = @source
            """, new
        {
            source = sourceTicketId,
            target = targetTicketId,
            mergedStatusId,
            actorUserId,
        }, tx, cancellationToken: ct));

        // Bump the target's updated_utc so list views surface it.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @target",
            new { target = targetTicketId }, tx, cancellationToken: ct));

        // System-note timeline events on both tickets so the audit story is
        // self-contained even when the audit_log row isn't surfaced in the UI.
        var sourceNoteMeta = JsonSerializer.Serialize(new
        {
            mergedIntoTicketId = targetTicketId.ToString(),
            mergedIntoTicketNumber = targetNumber,
            actorUserId = actorUserId.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal, body_text)
            VALUES (@source, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            source = sourceTicketId,
            actorUserId,
            metadata = sourceNoteMeta,
            body = $"Merged into #{targetNumber}.",
        }, tx, cancellationToken: ct));

        var targetNoteMeta = JsonSerializer.Serialize(new
        {
            mergedFromTicketId = sourceTicketId.ToString(),
            mergedFromTicketNumber = source.Number,
            actorUserId = actorUserId.ToString(),
            movedEventCount = moved,
            movedTimesheetEntryCount = movedTimesheetEntries,
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal, body_text)
            VALUES (@target, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            target = targetTicketId,
            actorUserId,
            metadata = targetNoteMeta,
            body = $"Ticket #{source.Number} was merged into this ticket.",
        }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new MergeResult(true, moved, source.Number, targetNumber, crossCustomer, null);
    }

    private sealed class MergeLockedRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public Guid RequesterContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
    }

    public async Task<IReadOnlyList<SplitChildTicket>> GetSplitChildrenAsync(Guid parentTicketId, CancellationToken ct)
    {
        // Sparse index ix_tickets_split_from makes this an index-only scan.
        // Returns id+number pairs so the banner can render clickable links to
        // the children. Order by split_from_utc so the "Split into #A, #B"
        // strip reads in the chronological order the agent created them.
        const string sql = """
            SELECT id AS Id, number AS Number FROM tickets
            WHERE split_from_ticket_id = @parentTicketId
              AND is_deleted = FALSE
            ORDER BY split_from_utc NULLS LAST, number
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SplitChildTicket>(
            new CommandDefinition(sql, new { parentTicketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SplitResult?> SplitAsync(
        Guid sourceTicketId,
        long sourceMailEventId,
        string newSubject,
        Guid actorUserId,
        string? overrideBodyHtml,
        string? overrideBodyText,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the source row first so a concurrent merge/delete can't race us.
        const string lockSql = """
            SELECT id AS Id, number AS Number, queue_id AS QueueId,
                   requester_contact_id AS RequesterContactId,
                   company_id AS CompanyId,
                   awaiting_company_assignment AS AwaitingCompanyAssignment,
                   company_resolved_via AS CompanyResolvedVia,
                   merged_into_ticket_id AS MergedIntoTicketId
            FROM tickets WHERE id = @sourceTicketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var source = await conn.QueryFirstOrDefaultAsync<SplitSourceRow>(
            new CommandDefinition(lockSql, new { sourceTicketId }, tx, cancellationToken: ct));
        if (source is null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, 0, SplitFailureReason.SourceNotFound);
        }
        if (source.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.SourceMerged);
        }

        // Verify the mail event belongs to this ticket and is the right type.
        // We pull the body and the linked mail row in one shot so we can stamp
        // a system-note that includes the original mail subject.
        const string mailEventSql = """
            SELECT e.id AS Id, e.event_type AS EventType,
                   e.body_text AS BodyText, e.body_html AS BodyHtml,
                   e.author_contact_id AS AuthorContactId,
                   e.created_utc AS CreatedUtc,
                   m.id AS MailMessageId, m.subject AS MailSubject
            FROM ticket_events e
            LEFT JOIN mail_messages m ON m.ticket_event_id = e.id
            WHERE e.id = @sourceMailEventId AND e.ticket_id = @sourceTicketId
            """;
        var mailEvent = await conn.QueryFirstOrDefaultAsync<SplitMailEventRow>(
            new CommandDefinition(mailEventSql, new { sourceMailEventId, sourceTicketId }, tx, cancellationToken: ct));
        if (mailEvent is null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.MailEventNotFound);
        }
        if (!string.Equals(mailEvent.EventType, "MailReceived", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.NotAMailEvent);
        }

        // Resolve system defaults for the new ticket. Priority + status carry
        // is_default; queue does not, so we fall back to the lowest-sort active
        // queue. All three must exist for the new ticket to be valid.
        var defaultStatusId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM statuses WHERE is_default = TRUE AND is_active = TRUE LIMIT 1",
            transaction: tx, cancellationToken: ct));
        var defaultPriorityId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM priorities WHERE is_default = TRUE AND is_active = TRUE LIMIT 1",
            transaction: tx, cancellationToken: ct));
        var defaultQueueId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM queues WHERE is_active = TRUE ORDER BY sort_order, name LIMIT 1",
            transaction: tx, cancellationToken: ct));
        if (defaultStatusId is null || defaultPriorityId is null || defaultQueueId is null)
        {
            await tx.RollbackAsync(ct);
            return new SplitResult(false, null, null, source.Number, SplitFailureReason.DefaultsMissing);
        }

        // Create the new ticket. Requester + company carry over from the source
        // (the email came from the same person), but queue/priority/status are
        // system defaults so the agent can re-triage explicitly. Source field
        // 'Split' marks the intake channel for reporting.
        const string insertTicketSql = """
            INSERT INTO tickets (subject, requester_contact_id, queue_id,
                                 status_id, priority_id, source,
                                 company_id, awaiting_company_assignment, company_resolved_via,
                                 split_from_ticket_id, split_from_utc, split_from_user_id)
            VALUES (@Subject, @RequesterContactId, @QueueId,
                    @StatusId, @PriorityId, 'Split',
                    @CompanyId, @AwaitingCompanyAssignment, @CompanyResolvedVia,
                    @SplitFromTicketId, now(), @SplitFromUserId)
            RETURNING id, number
            """;
        var newRow = await conn.QueryFirstAsync<(Guid Id, long Number)>(
            new CommandDefinition(insertTicketSql, new
            {
                Subject = newSubject,
                source.RequesterContactId,
                QueueId = defaultQueueId.Value,
                StatusId = defaultStatusId.Value,
                PriorityId = defaultPriorityId.Value,
                source.CompanyId,
                source.AwaitingCompanyAssignment,
                source.CompanyResolvedVia,
                SplitFromTicketId = sourceTicketId,
                SplitFromUserId = actorUserId,
            }, tx, cancellationToken: ct));

        // Description = the mail body. The caller passes the cid-rewritten HTML
        // (mail-timeline-enricher output) so inline `cid:` references resolve
        // against the source mail's attachment URLs. Falls back to the raw
        // event body when the enricher didn't produce a rewrite (e.g. a mail
        // without inline attachments).
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
            VALUES (@ticketId, @bodyText, @bodyHtml)
            """, new
        {
            ticketId = newRow.Id,
            bodyText = overrideBodyText ?? mailEvent.BodyText ?? string.Empty,
            bodyHtml = overrideBodyHtml ?? mailEvent.BodyHtml,
        }, tx, cancellationToken: ct));

        // Created event mirrors what CreateAsync writes — same shape so list/
        // detail readers don't need to special-case split origins.
        var createdMeta = JsonSerializer.Serialize(new
        {
            source = "Split",
            splitFromTicketId = sourceTicketId.ToString(),
            splitFromTicketNumber = source.Number,
            splitFromMailEventId = sourceMailEventId,
            splitFromMailMessageId = mailEvent.MailMessageId?.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id,
                                       body_text, body_html, metadata, is_internal)
            VALUES (@ticketId, 'Created', @authorContactId, NULL, NULL, @metadata::jsonb, FALSE)
            """, new
        {
            ticketId = newRow.Id,
            authorContactId = source.RequesterContactId,
            metadata = createdMeta,
        }, tx, cancellationToken: ct));

        // System-note on the new ticket pointing back to the source.
        var newNoteMeta = JsonSerializer.Serialize(new
        {
            splitFromTicketId = sourceTicketId.ToString(),
            splitFromTicketNumber = source.Number,
            splitFromMailEventId = sourceMailEventId,
            actorUserId = actorUserId.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id,
                                       metadata, is_internal, body_text)
            VALUES (@ticketId, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            ticketId = newRow.Id,
            actorUserId,
            metadata = newNoteMeta,
            body = $"Split from #{source.Number}.",
        }, tx, cancellationToken: ct));

        // System-note on the source ticket pointing forward to the new one.
        var sourceNoteMeta = JsonSerializer.Serialize(new
        {
            splitIntoTicketId = newRow.Id.ToString(),
            splitIntoTicketNumber = newRow.Number,
            sourceMailEventId,
            actorUserId = actorUserId.ToString(),
        });
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id,
                                       metadata, is_internal, body_text)
            VALUES (@sourceTicketId, 'SystemNote', @actorUserId, @metadata::jsonb, FALSE, @body)
            """, new
        {
            sourceTicketId,
            actorUserId,
            metadata = sourceNoteMeta,
            body = $"Split into #{newRow.Number}.",
        }, tx, cancellationToken: ct));

        // Bump the source ticket's updated_utc so the list refreshes.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tickets SET updated_utc = now() WHERE id = @sourceTicketId",
            new { sourceTicketId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new SplitResult(true, newRow.Id, newRow.Number, source.Number, null);
    }

    private sealed class SplitSourceRow
    {
        public Guid Id { get; set; }
        public long Number { get; set; }
        public Guid QueueId { get; set; }
        public Guid RequesterContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public bool AwaitingCompanyAssignment { get; set; }
        public string? CompanyResolvedVia { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
    }

    private sealed class SplitMailEventRow
    {
        public long Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string? BodyText { get; set; }
        public string? BodyHtml { get; set; }
        public Guid? AuthorContactId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Guid? MailMessageId { get; set; }
        public string? MailSubject { get; set; }
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

    public async Task<IReadOnlyList<LinkedChildTicket>> GetChildTicketsAsync(Guid parentTicketId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, number AS Number
            FROM tickets
            WHERE parent_ticket_id = @parentTicketId
              AND is_deleted = FALSE
            ORDER BY number
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<LinkedChildTicket>(
            new CommandDefinition(sql, new { parentTicketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<ParentTicketSummary?> GetParentSummaryAsync(Guid ticketId, CancellationToken ct)
    {
        // Joins the parent row + the user that ran the link so the UI can
        // render "Main ticket #X (linked by alice@…)" without a second
        // round-trip. Returns null when this ticket has no parent.
        const string sql = """
            SELECT p.id           AS ParentTicketId,
                   p.number       AS ParentNumber,
                   u.email        AS LinkedByName,
                   t.parent_linked_utc AS LinkedUtc
            FROM tickets t
            JOIN tickets p ON p.id = t.parent_ticket_id
            LEFT JOIN users u ON u.id = t.parent_linked_by_user_id
            WHERE t.id = @ticketId
              AND t.parent_ticket_id IS NOT NULL
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ParentTicketSummary>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
    }

    public async Task<LinkParentResult> LinkParentAsync(
        Guid ticketId, Guid parentTicketId, Guid actorUserId, CancellationToken ct)
    {
        if (ticketId == parentTicketId)
            return new LinkParentResult(false, LinkParentFailureReason.SameTicket);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock both rows so a concurrent re-parent on either side can't
        // race the cycle check. SELECT FOR UPDATE blocks until the other
        // tx commits, then we re-read the up-to-date parent chain.
        const string readSql = """
            SELECT id AS Id, merged_into_ticket_id AS MergedIntoTicketId,
                   parent_ticket_id AS ParentTicketId
            FROM tickets
            WHERE id = @id AND is_deleted = FALSE
            FOR UPDATE
            """;
        var source = await conn.QueryFirstOrDefaultAsync<LinkParentRow>(
            new CommandDefinition(readSql, new { id = ticketId }, tx, cancellationToken: ct));
        if (source is null) { await tx.RollbackAsync(ct); return new LinkParentResult(false, LinkParentFailureReason.SourceNotFound); }
        if (source.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new LinkParentResult(false, LinkParentFailureReason.SourceIsMerged);
        }

        var parent = await conn.QueryFirstOrDefaultAsync<LinkParentRow>(
            new CommandDefinition(readSql, new { id = parentTicketId }, tx, cancellationToken: ct));
        if (parent is null) { await tx.RollbackAsync(ct); return new LinkParentResult(false, LinkParentFailureReason.ParentNotFound); }
        if (parent.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new LinkParentResult(false, LinkParentFailureReason.ParentIsMerged);
        }

        // Cycle check: walk the candidate parent's chain upward. If we
        // hit `ticketId` along the way, accepting the link would create a
        // cycle. Bounded at 50 hops; the index on parent_ticket_id makes
        // each hop a single indexed PK lookup. Typical depth is 1.
        var cursor = parent.ParentTicketId;
        const int maxDepth = 50;
        for (var i = 0; i < maxDepth && cursor is Guid pid; i++)
        {
            if (pid == ticketId)
            {
                await tx.RollbackAsync(ct);
                return new LinkParentResult(false, LinkParentFailureReason.WouldCycle);
            }
            cursor = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT parent_ticket_id FROM tickets WHERE id = @id",
                new { id = pid }, tx, cancellationToken: ct));
        }

        const string updateSql = """
            UPDATE tickets
               SET parent_ticket_id         = @parentTicketId,
                   parent_linked_utc        = now(),
                   parent_linked_by_user_id = @actorUserId,
                   updated_utc              = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId, parentTicketId, actorUserId }, tx, cancellationToken: ct));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return new LinkParentResult(false, LinkParentFailureReason.SourceNotFound);
        }

        // Timeline event on the child so the audit trail is complete.
        // The parent ticket's "Sub tickets" list is derived from the FK,
        // not from events, so no event is written on that side.
        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            parentTicketId,
            parentNumber = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT number FROM tickets WHERE id = @id", new { id = parentTicketId }, tx, cancellationToken: ct)),
            previousParentTicketId = source.ParentTicketId,
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ParentLinked', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new LinkParentResult(true, null);
    }

    public async Task<bool> UnlinkParentAsync(Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Snapshot the previous parent so the event metadata carries it.
        // If the ticket has no parent we treat it as "nothing to do" — the
        // endpoint maps that to 404 so an idempotent client doesn't get a
        // false success.
        const string readSql = """
            SELECT parent_ticket_id AS ParentTicketId
            FROM tickets
            WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var prevParent = await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (prevParent is null) { await tx.RollbackAsync(ct); return false; }

        const string updateSql = """
            UPDATE tickets
               SET parent_ticket_id         = NULL,
                   parent_linked_utc        = NULL,
                   parent_linked_by_user_id = NULL,
                   updated_utc              = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId }, tx, cancellationToken: ct));
        if (rows == 0) { await tx.RollbackAsync(ct); return false; }

        var metadata = System.Text.Json.JsonSerializer.Serialize(new { previousParentTicketId = prevParent });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ParentUnlinked', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return true;
    }

    private sealed class LinkParentRow
    {
        public Guid Id { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
        public Guid? ParentTicketId { get; set; }
    }
}
