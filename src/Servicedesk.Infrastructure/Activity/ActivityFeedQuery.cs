using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Activity;

public sealed class ActivityFeedQuery : IActivityFeedQuery
{
    // Raw projection used by GetByIdAsync (the SignalR broadcast path):
    // returns the ticket subject/number unmasked plus the queue id, so the
    // broadcaster can mask the row per recipient in code. The LEFT JOIN on
    // tickets is keyed on entity_id::uuid which is safe because
    // entity_type='ticket' rows store the ticket's UUID as text — the
    // WHERE clause guards the cast so non-ticket rows never attempt it.
    private const string RawSelectColumns = """
        SELECT
            a.id              AS Id,
            a.occurred_utc    AS OccurredUtc,
            a.agent_id        AS AgentId,
            u.email           AS AgentEmail,
            a.agent_role      AS AgentRole,
            a.event_type      AS EventType,
            a.entity_type     AS EntityType,
            a.entity_id       AS EntityId,
            a.entity_extra    AS EntityExtra,
            a.summary         AS Summary,
            a.metadata::text  AS MetadataJson,
            t.number          AS TicketNumber,
            t.subject         AS TicketSubject,
            t.queue_id        AS TicketQueueId
        FROM agent_activity_events a
        JOIN users u ON u.id = a.agent_id
        LEFT JOIN tickets t
            ON a.entity_type = 'ticket'
            AND t.id = CASE WHEN a.entity_id ~ '^[0-9a-fA-F-]{36}$'
                            THEN a.entity_id::uuid ELSE NULL END
        """;

    // Viewer-masked projection used by the list reads. A ticket the viewer
    // cannot access has its number/subject nulled, its entity_id stripped
    // (so the client cannot deep-link) and TicketRestricted set, while the
    // event row itself stays visible. @IsAdmin short-circuits to full
    // visibility; @Accessible is the viewer's granted queue ids.
    private const string Visible = "(@IsAdmin OR t.queue_id = ANY(@Accessible))";

    private const string MaskedSelectColumns = $$"""
        SELECT
            a.id              AS Id,
            a.occurred_utc    AS OccurredUtc,
            a.agent_id        AS AgentId,
            u.email           AS AgentEmail,
            a.agent_role      AS AgentRole,
            a.event_type      AS EventType,
            a.entity_type     AS EntityType,
            CASE WHEN t.id IS NOT NULL AND NOT {{Visible}}
                 THEN NULL ELSE a.entity_id END AS EntityId,
            a.entity_extra    AS EntityExtra,
            a.summary         AS Summary,
            a.metadata::text  AS MetadataJson,
            CASE WHEN {{Visible}} THEN t.number END  AS TicketNumber,
            CASE WHEN {{Visible}} THEN t.subject END AS TicketSubject,
            (t.id IS NOT NULL AND NOT {{Visible}})   AS TicketRestricted
        FROM agent_activity_events a
        JOIN users u ON u.id = a.agent_id
        LEFT JOIN tickets t
            ON a.entity_type = 'ticket'
            AND t.id = CASE WHEN a.entity_id ~ '^[0-9a-fA-F-]{36}$'
                            THEN a.entity_id::uuid ELSE NULL END
        """;

    private readonly NpgsqlDataSource _dataSource;

    public ActivityFeedQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ActivityFeedEntry>> ListRecentAsync(
        QueueAccessScope viewer,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(limit, 1, 100);
        var sql = MaskedSelectColumns + " ORDER BY a.occurred_utc DESC, a.id DESC LIMIT @Limit";
        var parameters = new DynamicParameters();
        parameters.Add("Limit", clamped);
        AddViewerParameters(parameters, viewer);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ActivityFeedEntry>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<ActivityFeedPage> ListAsync(
        QueueAccessScope viewer,
        ActivityFeedFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var limit = Math.Clamp(filter.Limit, 1, 200);

        var sql = MaskedSelectColumns + "\nWHERE 1 = 1";
        var parameters = new DynamicParameters();
        AddViewerParameters(parameters, viewer);

        if (filter.AgentId is not null)
        {
            sql += " AND a.agent_id = @AgentId";
            parameters.Add("AgentId", filter.AgentId.Value);
        }
        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            sql += " AND a.event_type = @EventType";
            parameters.Add("EventType", filter.EventType);
        }
        if (filter.FromUtc is not null)
        {
            sql += " AND a.occurred_utc >= @FromUtc";
            parameters.Add("FromUtc", filter.FromUtc.Value);
        }
        if (filter.ToUtc is not null)
        {
            sql += " AND a.occurred_utc <= @ToUtc";
            parameters.Add("ToUtc", filter.ToUtc.Value);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Only match the ticket subject when the viewer may see it,
            // otherwise a subject search would surface (and rank) rows
            // whose ticket sits in a queue the viewer has no access to.
            sql += $" AND (a.summary ILIKE @Search OR u.email ILIKE @Search"
                 + $" OR (CASE WHEN {Visible} THEN t.subject END) ILIKE @Search)";
            parameters.Add("Search", $"%{filter.Search.Trim()}%");
        }
        if (filter.CursorId is not null)
        {
            sql += " AND a.id < @CursorId";
            parameters.Add("CursorId", filter.CursorId.Value);
        }

        sql += " ORDER BY a.occurred_utc DESC, a.id DESC LIMIT @Limit";
        parameters.Add("Limit", limit + 1);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ActivityFeedEntry>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        long? nextCursor = null;
        if (rows.Count > limit)
        {
            nextCursor = rows[limit - 1].Id;
            rows = rows.Take(limit).ToList();
        }
        return new ActivityFeedPage(rows, nextCursor);
    }

    public async Task<ActivityFeedEntry?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = RawSelectColumns + " WHERE a.id = @Id";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<ActivityFeedEntry>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
    }

    private static void AddViewerParameters(DynamicParameters parameters, QueueAccessScope viewer)
    {
        parameters.Add("IsAdmin", viewer.IsAdmin);
        parameters.Add("Accessible", viewer.ToArray());
    }
}
