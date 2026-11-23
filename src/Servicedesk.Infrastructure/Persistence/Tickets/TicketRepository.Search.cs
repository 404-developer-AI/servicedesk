using Servicedesk.Infrastructure.Access;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public sealed partial class TicketRepository
{
    private static readonly Dictionary<string, string> SortFieldMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["updatedUtc"]     = "t.updated_utc",
        ["createdUtc"]     = "t.created_utc",
        ["dueUtc"]         = "COALESCE(t.due_utc, '9999-12-31'::timestamptz)",
        ["priorityLevel"]  = "p.level",
        ["number"]         = "t.number",
        // Tickets without a pending-till sort last on ASC (and first on DESC),
        // same NULL-handling as dueUtc above. In normal operation a non-null
        // value is always in the future — the scheduler clears it on elapse.
        ["pendingTillUtc"] = "COALESCE(t.pending_till_utc, '9999-12-31'::timestamptz)",
        ["subject"]        = "t.subject",
        ["statusName"]     = "s.name",
        ["queueName"]      = "q.name",
        ["assigneeEmail"]  = "COALESCE(u.email, '')",
        ["requesterEmail"] = "c.email",
        ["companyName"]    = "COALESCE(co.name, '')",
        ["categoryName"]   = "COALESCE(cat.name, '')",
        // v0.1.18 — only valid with the TimeLoggedJoin below; SearchAsync
        // adds it whenever this sort field is requested.
        ["timeLogged"]     = "COALESCE(tl.minutes, 0)",
    };

    /// v0.1.18 — "Time logged" column: every timesheet entry on the ticket,
    /// all agents, invoiced flag ignored (same total as the Time-logged panel
    /// and the hour-limit alert). Display only: a correlated sum, evaluated
    /// for the returned rows via the partial ix_timesheet_entries_ticket.
    private const string TimeLoggedExpr =
        "(SELECT COALESCE(SUM(e.minutes), 0)::int FROM timesheet_entries e WHERE e.ticket_id = t.id)";

    /// Sorting on it needs the value for every candidate row, so it is
    /// aggregated once and hash-joined instead of looping per ticket.
    private const string TimeLoggedJoin =
        " LEFT JOIN (SELECT ticket_id, SUM(minutes)::int AS minutes FROM timesheet_entries" +
        " WHERE ticket_id IS NOT NULL GROUP BY ticket_id) tl ON tl.ticket_id = t.id";
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
            t.pending_till_utc              AS PendingTillUtc,
            t.awaiting_company_assignment   AS AwaitingCompanyAssignment,
            t.company_resolved_via          AS CompanyResolvedVia,
            t.ticket_type_id                AS TicketTypeId,
            t.checklist_required_total      AS ChecklistRequiredTotal,
            t.checklist_required_done       AS ChecklistRequiredDone,
            t.is_callback                   AS IsCallback,
            t.is_research                   AS IsResearch,
            {0}                             AS TimeLoggedMinutes
        FROM tickets t
        JOIN queues     q ON q.id = t.queue_id
        JOIN statuses   s ON s.id = t.status_id
        JOIN priorities p ON p.id = t.priority_id
        JOIN contacts   c ON c.id = t.requester_contact_id
        LEFT JOIN companies  co  ON co.id  = t.company_id
        LEFT JOIN users      u   ON u.id   = t.assignee_user_id
        LEFT JOIN categories cat ON cat.id = t.category_id{1}
        """;

    /// v0.0.101 — "1234" or "#1234" (surrounding whitespace ignored) is a
    /// ticket-number probe; anything else (empty, mixed, too long for a
    /// bigint) is not. Kept strict on purpose: the number arm of the search
    /// hit-set compares against the indexed `tickets.number` column with a
    /// typed parameter, so the input must be a clean integer.
    internal static long? TryParseTicketNumber(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var span = search.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length is 0 or > 18) return null;
        foreach (var c in span)
            if (c is < '0' or > '9') return null;
        return long.TryParse(span, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    public async Task<TicketPage> SearchAsync(
        TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct)
    {
        var sortOnTimeLogged = string.Equals(query.SortField, "timeLogged", StringComparison.OrdinalIgnoreCase);
        var timeProjection = sortOnTimeLogged ? "COALESCE(tl.minutes, 0)"
            : query.IncludeTimeLogged ? TimeLoggedExpr
            : "NULL::int";
        var sql = new StringBuilder(ListSelect
            .Replace("{0}", timeProjection)
            .Replace("{1}", sortOnTimeLogged ? TimeLoggedJoin : string.Empty));
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
        if (query.ProjectsOnly) sql.Append(" AND t.is_project = TRUE");
        // v0.1.17 — "Call-back / Research tickets only" view filters. Both
        // set = tickets carrying either flag (a view is one list, not an
        // intersection the agent would have to reason about).
        if (query.CallbacksOnly && query.ResearchOnly) sql.Append(" AND (t.is_callback = TRUE OR t.is_research = TRUE)");
        else if (query.CallbacksOnly) sql.Append(" AND t.is_callback = TRUE");
        else if (query.ResearchOnly) sql.Append(" AND t.is_research = TRUE");

        // Queue-access enforcement (shared rule): only the queues the caller
        // may see; null (admin) = no filter.
        sql.Append(" AND ").Append(QueueAccessScope.FromNullable(query.AccessibleQueueIds).SqlPredicate("t.queue_id"));

        var searchNumber = TryParseTicketNumber(query.Search);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Subject (tickets.search_vector) OR exact ticket number OR body
            // (ticket_event_search.search_vector — sidecar fed by trigger).
            //
            // v0.0.101: the three sources are unioned into one hit-set the
            // main query semi-joins on, instead of `A OR B OR EXISTS(...)`.
            // The OR form could not be planned as one index path — a
            // correlated EXISTS inside an OR is evaluated per candidate row
            // and `number::text = @x` casts the indexed column away — so a
            // bare search word fell back to a seq scan of tickets. Now each
            // arm is its own GIN / btree lookup and the number arm is only
            // emitted when the term actually parses as a ticket number
            // (parameter typed bigint, column untouched).
            sql.Append(" AND t.id IN (");
            sql.Append("SELECT id FROM tickets WHERE search_vector @@ plainto_tsquery('simple', @Search)");
            if (searchNumber is not null)
                sql.Append(" UNION SELECT id FROM tickets WHERE number = @SearchNumber");
            sql.Append(" UNION SELECT ticket_id FROM ticket_event_search WHERE search_vector @@ plainto_tsquery('simple', @Search)");
            sql.Append(")");
        }

        // v0.1.18 — per-view search box, "Full" mode. Same UNION hit-set shape
        // as above (one index path per arm), but prefix-matching like global
        // search and including the original description (ticket_bodies).
        var hasFullTs = !string.IsNullOrEmpty(query.FullSearchTsQuery);
        if (hasFullTs || query.FullSearchNumber is not null)
        {
            var arms = new List<string>();
            if (hasFullTs)
            {
                arms.Add("SELECT id FROM tickets WHERE search_vector @@ to_tsquery('simple', @FullSearchTsQuery)");
                arms.Add("SELECT ticket_id FROM ticket_event_search WHERE search_vector @@ to_tsquery('simple', @FullSearchTsQuery)");
                arms.Add("SELECT ticket_id FROM ticket_bodies WHERE body_search @@ to_tsquery('simple', @FullSearchTsQuery)");
            }
            if (query.FullSearchNumber is not null)
                arms.Add("SELECT id FROM tickets WHERE number = @FullSearchNumber");
            sql.Append(" AND t.id IN (").Append(string.Join(" UNION ", arms)).Append(')');
        }

        // v0.1.18 — "Columns" mode server fallback (whitelisted fields only).
        var columnParams = new DynamicParameters();
        TicketColumnSearch.Append(sql, columnParams, query.ColumnSearch, query.ColumnSearchFields);

        // Visibility enforcement. Never trust client-supplied scope — this is
        // resolved from the authenticated principal upstream. The filter
        // point exists so the future portal inherits it without a rewrite.
        switch (scope)
        {
            case VisibilityScope.Own:
                sql.Append(" AND t.requester_contact_id = @ViewerContactId");
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
        var anyFloat = query.PriorityFloat || query.CallbackFloat || query.ResearchFloat;
        var useOffset = hasDynamicSort || anyFloat || query.OpenFirst || query.StateBucketSort;

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
        if (query.StateBucketSort)
        {
            // Per-view "Open tickets first" (v0.0.95): New/Open above Pending
            // above Resolved/Closed; the secondary sort applies within each
            // bucket. Client-side grouping preserves row order per group, so
            // every group inherits this bucketing without extra work there.
            orderClauses.Add("(CASE WHEN s.state_category = 'Pending' THEN 1 WHEN s.state_category IN ('Resolved','Closed') THEN 2 ELSE 0 END) ASC");
        }
        if (anyFloat)
        {
            // Float buckets (v0.1.17), only for tickets still New or Open — a
            // ticket that moved to Pending/Resolved/Closed drops back into the
            // normal order, matching the grouped-list client bucketing.
            // Fixed precedence: Priority (non-default) > Call-back > Research;
            // a ticket lands in the first enabled bucket it qualifies for, so a
            // prio + call-back ticket sits only in the Priority float. Only the
            // enabled buckets are emitted; the CASE arms are constants, never
            // client input.
            var arms = new List<string>();
            if (query.PriorityFloat) arms.Add("WHEN NOT p.is_default THEN 0");
            if (query.CallbackFloat) arms.Add("WHEN t.is_callback THEN 1");
            if (query.ResearchFloat) arms.Add("WHEN t.is_research THEN 2");
            orderClauses.Add(
                "(CASE WHEN s.state_category IN ('New','Open') THEN (CASE " + string.Join(" ", arms) +
                " ELSE 3 END) ELSE 3 END) ASC");
            if (query.PriorityFloat)
                orderClauses.Add("CASE WHEN (NOT p.is_default) AND s.state_category IN ('New','Open') THEN p.level END ASC");
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
            SearchNumber = searchNumber,
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
            FullSearchTsQuery = query.FullSearchTsQuery ?? "",
            query.FullSearchNumber,
        };
        var dbParams = new DynamicParameters(parameters);
        dbParams.AddDynamicParams(columnParams);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TicketListItem>(
            new CommandDefinition(sql.ToString(), dbParams, cancellationToken: ct))).ToList();

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


    // Identical projection + base joins for every picker variant, so a row
    // renders the same whether it came from the user's recent list, the
    // global fill, or a search. Variant-specific JOINs/filters/ORDER BY are
    // appended after this fragment.
    private const string PickerSelectFrom = """
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
        """;

    public async Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(
        string? search,
        Guid excludeTicketId,
        IReadOnlyCollection<Guid>? accessibleQueueIds,
        Guid? recentForUserId,
        int limit,
        CancellationToken ct,
        bool projectsOnly = false)
    {
        // v0.0.105 — link-to-project dialog: only open project tickets are
        // valid targets. Composes with every arm below (recent / fill / search).
        var projectFilter = projectsOnly
            ? " AND t.is_project = TRUE AND s.state_category NOT IN ('Resolved','Closed')"
            : string.Empty;
        // Picker filters out tickets that are already merged so an agent can't
        // accidentally select a "tombstone" as the target. We exclude self
        // (no-op merge) and respect queue-access — admins pass null and skip
        // the filter. Limit is clamped to a sane range to keep typeahead snappy.
        var clampedLimit = Math.Clamp(limit, 1, 50);
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var queueFilter = " AND " + QueueAccessScope.FromNullable(accessibleQueueIds).SqlPredicate("t.queue_id");
        var queueParam = accessibleQueueIds as IEnumerable<Guid>;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Link-parent default view: the dialog opens with no search term, so
        // lead with the tickets THIS user most recently opened
        // (user_recent_tickets.added_utc is bumped to now() on every re-view),
        // newest first. The global "most recently updated" list backfills any
        // remaining slots so a user with little history still sees candidates.
        // The fill keeps using the (updated_utc DESC, id DESC) index — we never
        // sort the whole tickets table by the joined recency column.
        if (recentForUserId.HasValue && !hasSearch)
        {
            var recentSql = $"""
                {PickerSelectFrom}
                JOIN user_recent_tickets urt
                  ON urt.ticket_id = t.id AND urt.user_id = @RecentUserId
                WHERE t.is_deleted = FALSE
                  AND t.merged_into_ticket_id IS NULL
                  AND t.id <> @excludeTicketId{queueFilter}{projectFilter}
                ORDER BY urt.added_utc DESC
                LIMIT @Limit
                """;
            var recent = (await conn.QueryAsync<TicketPickerHit>(new CommandDefinition(recentSql, new
            {
                RecentUserId = recentForUserId.Value,
                excludeTicketId,
                AccessibleQueueIds = queueParam,
                Limit = clampedLimit,
            }, cancellationToken: ct))).ToList();

            if (recent.Count >= clampedLimit)
                return recent;

            // Backfill, skipping the ids we already have so nothing shows twice.
            var seenIds = recent.Select(r => r.Id).ToArray();
            var fillSql = $"""
                {PickerSelectFrom}
                WHERE t.is_deleted = FALSE
                  AND t.merged_into_ticket_id IS NULL
                  AND t.id <> @excludeTicketId
                  AND t.id <> ALL(@SeenIds){queueFilter}{projectFilter}
                ORDER BY t.updated_utc DESC, t.id DESC
                LIMIT @Fill
                """;
            var fill = await conn.QueryAsync<TicketPickerHit>(new CommandDefinition(fillSql, new
            {
                excludeTicketId,
                SeenIds = seenIds,
                AccessibleQueueIds = queueParam,
                Fill = clampedLimit - recent.Count,
            }, cancellationToken: ct));
            recent.AddRange(fill);
            return recent;
        }

        // Search path (and the merge picker, which never asks for recent-first):
        // full-text on subject OR exact ticket-number match — same shape as the
        // list endpoint so an agent can paste a number and find it.
        var sql = new StringBuilder(PickerSelectFrom);
        sql.Append("""

            WHERE t.is_deleted = FALSE
              AND t.merged_into_ticket_id IS NULL
              AND t.id <> @excludeTicketId
            """);
        sql.Append(queueFilter);
        sql.Append(projectFilter);
        // v0.0.101: number arm only when the term parses as a ticket number,
        // compared as a typed bigint (indexed) instead of casting the column.
        var pickerNumber = TryParseTicketNumber(search);
        if (hasSearch)
            sql.Append(pickerNumber is not null
                ? " AND (t.search_vector @@ plainto_tsquery('simple', @Search) OR t.number = @SearchNumber)"
                : " AND t.search_vector @@ plainto_tsquery('simple', @Search)");
        sql.Append(" ORDER BY t.updated_utc DESC, t.id DESC LIMIT @Limit");

        var rows = await conn.QueryAsync<TicketPickerHit>(new CommandDefinition(sql.ToString(), new
        {
            excludeTicketId,
            AccessibleQueueIds = queueParam,
            Search = search ?? string.Empty,
            SearchNumber = pickerNumber,
            Limit = clampedLimit,
        }, cancellationToken: ct));
        return rows.ToList();
    }
}
