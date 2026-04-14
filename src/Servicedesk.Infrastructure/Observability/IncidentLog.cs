using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Observability;

public sealed class IncidentLog : IIncidentLog
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);

    private readonly NpgsqlDataSource _ds;

    public IncidentLog(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task ReportAsync(
        string subsystem,
        IncidentSeverity severity,
        string message,
        string? details,
        string? contextJson,
        CancellationToken ct)
    {
        // Treat null/empty messages as "(no message)" — they still deserve a row.
        var msg = string.IsNullOrWhiteSpace(message) ? "(no message)" : message;
        var sev = severity.ToString();
        var context = string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson;

        // Try to bump an existing open row within the dedup window. If no row
        // was updated, insert a new one. Wrapped in a single round-trip via
        // CTE so concurrent reporters don't race.
        const string sql = """
            WITH bump AS (
                UPDATE incidents
                   SET last_occurred_utc = now(),
                       occurrence_count  = occurrence_count + 1,
                       details           = COALESCE(@details, details)
                 WHERE id = (
                     SELECT id FROM incidents
                      WHERE subsystem = @subsystem
                        AND severity  = @severity
                        AND message   = @message
                        AND acknowledged_utc IS NULL
                        AND last_occurred_utc >= now() - @window::interval
                      ORDER BY last_occurred_utc DESC
                      LIMIT 1
                      FOR UPDATE
                 )
                 RETURNING id
            )
            INSERT INTO incidents (subsystem, severity, message, details, context)
            SELECT @subsystem, @severity, @message, @details, @context::jsonb
             WHERE NOT EXISTS (SELECT 1 FROM bump);
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            subsystem,
            severity = sev,
            message = msg,
            details,
            context,
            window = $"{(int)DedupWindow.TotalSeconds} seconds",
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IncidentRow>> ListOpenAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id, subsystem, severity, message, details, context::text AS context_json,
                   first_occurred_utc, last_occurred_utc, occurrence_count,
                   acknowledged_utc, acknowledged_by_user_id
              FROM incidents
             WHERE acknowledged_utc IS NULL
             ORDER BY last_occurred_utc DESC
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<IncidentRow>> ListOpenRecentAsync(int take, CancellationToken ct)
    {
        const string sql = """
            SELECT id, subsystem, severity, message, details, context::text AS context_json,
                   first_occurred_utc, last_occurred_utc, occurrence_count,
                   acknowledged_utc, acknowledged_by_user_id
              FROM incidents
             WHERE acknowledged_utc IS NULL
             ORDER BY last_occurred_utc DESC
             LIMIT @take
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, new { take }, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<IncidentRow>> ListArchiveAsync(string? subsystem, int take, int skip, CancellationToken ct)
    {
        const string sql = """
            SELECT id, subsystem, severity, message, details, context::text AS context_json,
                   first_occurred_utc, last_occurred_utc, occurrence_count,
                   acknowledged_utc, acknowledged_by_user_id
              FROM incidents
             WHERE acknowledged_utc IS NOT NULL
               AND (@subsystem IS NULL OR subsystem = @subsystem)
             ORDER BY acknowledged_utc DESC
             LIMIT @take OFFSET @skip
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, new { subsystem, take, skip }, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<string?> AcknowledgeAsync(long id, Guid userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE incidents
               SET acknowledged_utc = now(),
                   acknowledged_by_user_id = @userId
             WHERE id = @id AND acknowledged_utc IS NULL
             RETURNING subsystem
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }

    public async Task<int> AcknowledgeSubsystemAsync(string subsystem, Guid userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE incidents
               SET acknowledged_utc = now(),
                   acknowledged_by_user_id = @userId
             WHERE subsystem = @subsystem AND acknowledged_utc IS NULL
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { subsystem, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, IncidentSeverity>> GetOpenBySubsystemAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT subsystem,
                   MAX(CASE severity WHEN 'Critical' THEN 2 ELSE 1 END) AS max_sev
              FROM incidents
             WHERE acknowledged_utc IS NULL
             GROUP BY subsystem
            """;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        var dict = new Dictionary<string, IncidentSeverity>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            string key = (string)r.subsystem;
            int sev = Convert.ToInt32(r.max_sev);
            dict[key] = sev >= 2 ? IncidentSeverity.Critical : IncidentSeverity.Warning;
        }
        return dict;
    }

    private static IncidentRow Map(dynamic r) => new(
        Id: (long)r.id,
        Subsystem: (string)r.subsystem,
        Severity: Enum.Parse<IncidentSeverity>((string)r.severity),
        Message: (string)r.message,
        Details: (string?)r.details,
        ContextJson: (string)r.context_json,
        FirstOccurredUtc: (DateTime)r.first_occurred_utc,
        LastOccurredUtc: (DateTime)r.last_occurred_utc,
        OccurrenceCount: (int)r.occurrence_count,
        AcknowledgedUtc: (DateTime?)r.acknowledged_utc,
        AcknowledgedByUserId: (Guid?)r.acknowledged_by_user_id);
}
