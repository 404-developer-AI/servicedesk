using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Audit;

public sealed class AuditQueryService : IAuditQuery
{
    private readonly NpgsqlDataSource _dataSource;

    public AuditQueryService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 200);

        var sql = """
            SELECT id, utc, actor, actor_role AS ActorRole, event_type AS EventType,
                   target, client_ip AS ClientIp, user_agent AS UserAgent,
                   payload::text AS PayloadJson, prev_hash AS PrevHash, entry_hash AS EntryHash
            FROM audit_log
            WHERE 1 = 1
            """;
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            sql += " AND event_type = @EventType";
            parameters.Add("EventType", query.EventType);
        }
        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            sql += " AND actor = @Actor";
            parameters.Add("Actor", query.Actor);
        }
        if (query.FromUtc is not null)
        {
            sql += " AND utc >= @FromUtc";
            parameters.Add("FromUtc", query.FromUtc.Value);
        }
        if (query.ToUtc is not null)
        {
            sql += " AND utc <= @ToUtc";
            parameters.Add("ToUtc", query.ToUtc.Value);
        }
        if (query.CursorId is not null)
        {
            sql += " AND id < @CursorId";
            parameters.Add("CursorId", query.CursorId.Value);
        }

        sql += " ORDER BY id DESC LIMIT @Limit";
        parameters.Add("Limit", limit + 1); // fetch one extra to detect next page

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<AuditLogEntry>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        long? nextCursor = null;
        if (rows.Count > limit)
        {
            nextCursor = rows[limit - 1].Id;
            rows = rows.Take(limit).ToList();
        }

        return new AuditPage(rows, nextCursor);
    }

    public async Task<AuditPage> ListForContactAsync(
        Guid contactId, long? cursorId, int limit, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        const string baseSql = """
            SELECT id, utc, actor, actor_role AS ActorRole, event_type AS EventType,
                   target, client_ip AS ClientIp, user_agent AS UserAgent,
                   payload::text AS PayloadJson, prev_hash AS PrevHash, entry_hash AS EntryHash
            FROM audit_log
            WHERE (target = @ContactText OR payload->>'contactId' = @ContactText)
            """;
        var sql = baseSql;
        var parameters = new DynamicParameters();
        parameters.Add("ContactText", contactId.ToString());
        if (cursorId is not null)
        {
            sql += " AND id < @CursorId";
            parameters.Add("CursorId", cursorId.Value);
        }
        sql += " ORDER BY id DESC LIMIT @Limit";
        parameters.Add("Limit", clamped + 1);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<AuditLogEntry>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        long? nextCursor = null;
        if (rows.Count > clamped)
        {
            nextCursor = rows[clamped - 1].Id;
            rows = rows.Take(clamped).ToList();
        }
        return new AuditPage(rows, nextCursor);
    }

    public async Task<AuditLogEntry?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, utc, actor, actor_role AS ActorRole, event_type AS EventType,
                   target, client_ip AS ClientIp, user_agent AS UserAgent,
                   payload::text AS PayloadJson, prev_hash AS PrevHash, entry_hash AS EntryHash
            FROM audit_log WHERE id = @id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<AuditLogEntry>(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (eventTypes is null || eventTypes.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        // ix_audit_log_event_type covers (event_type); the planner adds the
        // utc range filter on top. ANY(@types) keeps the parameter list a
        // single bind instead of expanding to N OR-clauses.
        const string sql = """
            SELECT event_type, COUNT(*)::int AS cnt
              FROM audit_log
             WHERE event_type = ANY(@types)
               AND utc >= @from
               AND utc <  @to
             GROUP BY event_type
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new
        {
            types = eventTypes.ToArray(),
            from = fromUtc,
            to = toUtc,
        }, cancellationToken: cancellationToken));

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            result[(string)r.event_type] = (int)r.cnt;
        }
        return result;
    }
}
