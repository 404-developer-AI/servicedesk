using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Insights.Rewind;

public interface IRewindStore
{
    Task<IReadOnlyList<RewindTrackedView>> ListTrackedViewsAsync(CancellationToken ct);
    Task<RewindLatest?> GetLatestAsync(Guid viewId, CancellationToken ct);

    /// Stores a changed capture. A second insert for the same slot is a
    /// no-op (unique (view, captured)), so an overlapping run can't double-write.
    Task InsertAsync(RewindCapture capture, CancellationToken ct);

    /// Unchanged capture: extend the latest row to cover <paramref name="slotUtc"/>.
    Task TouchAsync(long id, DateTime slotUtc, CancellationToken ct);

    /// Rows (without items) whose coverage overlaps [from, to].
    Task<IReadOnlyList<RewindSeriesRow>> GetSeriesAsync(Guid viewId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// The row in force at <paramref name="atUtc"/> (latest captured at or before it).
    Task<RewindSnapshotRow?> GetAtAsync(Guid viewId, DateTime atUtc, CancellationToken ct);

    /// The items of one stored row (to diff the next capture against).
    Task<string?> GetItemsAsync(long id, CancellationToken ct);

    /// Why each ticket left a view since <paramref name="sinceUtc"/>: see
    /// <see cref="RewindLeaveReason"/>.
    Task<IReadOnlyDictionary<Guid, string>> ClassifyLeaversAsync(
        IReadOnlyCollection<Guid> ticketIds, DateTime sinceUtc, CancellationToken ct);

    /// Of <paramref name="ticketIds"/>, the ones deleted since.
    Task<IReadOnlySet<Guid>> GetDeletedAsync(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct);
}

public sealed class RewindStore : IRewindStore
{
    private readonly NpgsqlDataSource _dataSource;

    public RewindStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<RewindTrackedView>> ListTrackedViewsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, filters::text AS FiltersJson, display_config::text AS DisplayConfigJson
            FROM views
            WHERE rewind_tracked = TRUE
            ORDER BY sort_order, name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RewindTrackedView>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<RewindLatest?> GetLatestAsync(Guid viewId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, captured_utc AS CapturedUtc, last_seen_utc AS LastSeenUtc, interval_minutes AS IntervalMinutes,
                   content_hash AS ContentHash
            FROM rewind_snapshots
            WHERE view_id = @viewId
            ORDER BY captured_utc DESC
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<RewindLatest>(new CommandDefinition(sql, new { viewId }, cancellationToken: ct));
    }

    public async Task InsertAsync(RewindCapture c, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO rewind_snapshots
                (view_id, captured_utc, last_seen_utc, interval_minutes, ticket_count, truncated,
                 content_hash, groups, counts, items, added, removed)
            VALUES
                (@ViewId, @SlotUtc, @SlotUtc, @IntervalMinutes, @TicketCount, @Truncated,
                 @ContentHash, @GroupsJson::jsonb, @CountsJson::jsonb, @ItemsJson::jsonb,
                 @AddedJson::jsonb, @RemovedJson::jsonb)
            ON CONFLICT (view_id, captured_utc) DO NOTHING
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, c, cancellationToken: ct));
    }

    public async Task TouchAsync(long id, DateTime slotUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE rewind_snapshots SET last_seen_utc = @slotUtc
            WHERE id = @id AND last_seen_utc < @slotUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, slotUtc }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RewindSeriesRow>> GetSeriesAsync(
        Guid viewId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        // A row covers [captured, last_seen + interval). Rows exist only per
        // change, so a view holds a few thousand at most within retention;
        // the (view, captured) index bounds the upper edge, and items (the
        // large column) is never selected here.
        const string sql = """
            SELECT captured_utc AS CapturedUtc, last_seen_utc AS LastSeenUtc, interval_minutes AS IntervalMinutes,
                   groups::text AS GroupsJson, counts::text AS CountsJson,
                   added::text AS AddedJson, removed::text AS RemovedJson
            FROM rewind_snapshots
            WHERE view_id = @viewId
              AND captured_utc <= @toUtc
              AND last_seen_utc + make_interval(mins => interval_minutes) > @fromUtc
            ORDER BY captured_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RewindSeriesRow>(
            new CommandDefinition(sql, new { viewId, fromUtc, toUtc }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<RewindSnapshotRow?> GetAtAsync(Guid viewId, DateTime atUtc, CancellationToken ct)
    {
        const string sql = """
            SELECT captured_utc AS CapturedUtc, last_seen_utc AS LastSeenUtc, interval_minutes AS IntervalMinutes,
                   truncated AS Truncated, groups::text AS GroupsJson, items::text AS ItemsJson,
                   added::text AS AddedJson, removed::text AS RemovedJson
            FROM rewind_snapshots
            WHERE view_id = @viewId AND captured_utc <= @atUtc
            ORDER BY captured_utc DESC
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<RewindSnapshotRow>(
            new CommandDefinition(sql, new { viewId, atUtc }, cancellationToken: ct));
    }

    public async Task<string?> GetItemsAsync(long id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT items::text FROM rewind_snapshots WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ClassifyLeaversAsync(
        IReadOnlyCollection<Guid> ticketIds, DateTime sinceUtc, CancellationToken ct)
    {
        if (ticketIds.Count == 0) return new Dictionary<Guid, string>();
        // Closed wins over a queue move in the same window (the ticket is
        // gone for good either way). Reason literals are constants.
        const string sql = """
            SELECT t.id AS Id,
                   CASE
                     WHEN t.merged_into_ticket_id IS NOT NULL
                       OR EXISTS (SELECT 1 FROM ticket_events e
                                  WHERE e.ticket_id = t.id AND e.created_utc > @sinceUtc
                                    AND e.event_type = 'StatusChange'
                                    AND e.metadata->>'toCategory' IN ('Resolved', 'Closed'))
                       THEN 'closed'
                     WHEN EXISTS (SELECT 1 FROM ticket_events e
                                  WHERE e.ticket_id = t.id AND e.created_utc > @sinceUtc
                                    AND e.event_type = 'QueueChange')
                       THEN 'queue'
                     ELSE 'other'
                   END AS Reason
            FROM tickets t
            WHERE t.id = ANY(@ids)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, string Reason)>(new CommandDefinition(
            sql, new { ids = ticketIds.ToArray(), sinceUtc }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Reason);
    }

    public async Task<IReadOnlySet<Guid>> GetDeletedAsync(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
    {
        if (ticketIds.Count == 0) return new HashSet<Guid>();
        const string sql = "SELECT id FROM tickets WHERE id = ANY(@ids) AND is_deleted = TRUE";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(
            new CommandDefinition(sql, new { ids = ticketIds.ToArray() }, cancellationToken: ct));
        return rows.ToHashSet();
    }
}
