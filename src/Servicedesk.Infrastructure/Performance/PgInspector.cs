using System.Text.Json;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Performance;

/// What the app role can read in PostgreSQL. Refreshed by every snapshot
/// run; the dashboard shows "not available — run deploy/update.sh" instead
/// of failing when a view is not readable.
public sealed class PgAccessState
{
    private volatile PgAccess _current = PgAccess.Unknown;

    public PgAccess Current => _current;

    public void Set(PgAccess access) => _current = access;
}

public sealed record PgAccess(
    bool Checked,
    int ServerVersionNum,
    string ServerVersion,
    bool PgMonitor,
    bool StatStatements,
    string? StatStatementsReason)
{
    public static readonly PgAccess Unknown = new(false, 0, "", false, false, "Not checked yet.");
}

/// Read-only queries against PostgreSQL's statistics views — live (for the
/// dashboard) and snapshot (for deltas over a period). Every statement here
/// reads catalog/statistics views or the perf_* tables only; the single
/// write to user tables is the whitelisted ANALYZE in <see cref="PerfToolbox"/>.
public sealed class PgInspector
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PgAccessState _access;

    public PgInspector(NpgsqlDataSource dataSource, PgAccessState access)
    {
        _dataSource = dataSource;
        _access = access;
    }

    public PgAccess Access => _access.Current;

    public async Task<PgAccess> CheckAccessAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var info = await conn.QuerySingleAsync<AccessRow>(new CommandDefinition("""
            SELECT current_setting('server_version_num')::int AS VersionNum,
                   current_setting('server_version') AS Version,
                   pg_has_role(current_user, 'pg_monitor', 'MEMBER') AS PgMonitor,
                   EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') AS HasStatements
            """, cancellationToken: ct));

        string? reason = null;
        var statements = false;
        if (!info.HasStatements)
        {
            reason = "The pg_stat_statements extension is not installed in this database. Run deploy/update.sh on the host.";
        }
        else
        {
            try
            {
                await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM pg_stat_statements LIMIT 1", cancellationToken: ct));
                statements = true;
                if (!info.PgMonitor)
                    reason = "Only this app's own queries are visible: the app role lacks pg_monitor. Run deploy/update.sh on the host.";
            }
            catch (PostgresException ex)
            {
                reason = ex.SqlState == "55000"
                    ? "pg_stat_statements is installed but not preloaded (shared_preload_libraries). Run deploy/update.sh on the host."
                    : "pg_stat_statements is not readable (" + ex.SqlState + ").";
            }
        }

        var access = new PgAccess(true, info.VersionNum, info.Version, info.PgMonitor, statements, reason);
        _access.Set(access);
        return access;
    }

    private sealed class AccessRow
    {
        public int VersionNum { get; set; }
        public string Version { get; set; } = "";
        public bool PgMonitor { get; set; }
        public bool HasStatements { get; set; }
    }

    // ------------------------------------------------------------ snapshots

    public sealed class StatementRow
    {
        public long QueryId { get; set; }
        public string Query { get; set; } = "";
        public long Calls { get; set; }
        public double TotalMs { get; set; }
        public long Rows { get; set; }
        public long BlksHit { get; set; }
        public long BlksRead { get; set; }
        public long TempWritten { get; set; }
        public double MeanMs { get; set; }
        public double StddevMs { get; set; }
        public double MaxMs { get; set; }
    }

    /// Top statements of this database, folded per queryid (pg_stat_statements
    /// keeps one row per user/toplevel combination).
    public async Task<IReadOnlyList<StatementRow>> ReadStatementsAsync(int topByTotal, int topByMean, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<StatementRow>(new CommandDefinition("""
            WITH s AS (
                SELECT queryid,
                       min(query) AS query,
                       sum(calls) AS calls,
                       sum(total_exec_time) AS total_ms,
                       sum(rows) AS rows,
                       sum(shared_blks_hit) AS blks_hit,
                       sum(shared_blks_read) AS blks_read,
                       sum(temp_blks_written) AS temp_written,
                       CASE WHEN sum(calls) > 0 THEN sum(total_exec_time) / sum(calls) ELSE 0 END AS mean_ms,
                       max(stddev_exec_time) AS stddev_ms,
                       max(max_exec_time) AS max_ms
                FROM pg_stat_statements
                WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND queryid IS NOT NULL
                GROUP BY queryid
            ),
            picked AS (
                (SELECT * FROM s ORDER BY total_ms DESC LIMIT @TopByTotal)
                UNION
                (SELECT * FROM s WHERE calls >= 5 ORDER BY mean_ms DESC LIMIT @TopByMean)
            )
            SELECT queryid AS QueryId, left(query, 6000) AS Query, calls AS Calls, total_ms AS TotalMs,
                   rows AS Rows, blks_hit AS BlksHit, blks_read AS BlksRead, temp_written AS TempWritten,
                   mean_ms AS MeanMs, stddev_ms AS StddevMs, max_ms AS MaxMs
            FROM picked
            """, new { TopByTotal = topByTotal, TopByMean = topByMean }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SnapshotStatementsAsync(DateTimeOffset ts, CancellationToken ct)
    {
        var rows = await ReadStatementsAsync(150, 50, ct);
        if (rows.Count == 0) return;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO perf_pg_statement_snapshot (ts_utc, queryid, calls, total_ms, rows, blks_hit, blks_read,
                                                    temp_written, mean_ms, stddev_ms, max_ms)
            SELECT @Ts, * FROM unnest(@Ids, @Calls, @Total, @Rows, @Hit, @Read, @Temp, @Mean, @Stddev, @Max)
            """, new
        {
            Ts = ts.UtcDateTime,
            Ids = rows.Select(r => r.QueryId).ToArray(),
            Calls = rows.Select(r => r.Calls).ToArray(),
            Total = rows.Select(r => r.TotalMs).ToArray(),
            Rows = rows.Select(r => r.Rows).ToArray(),
            Hit = rows.Select(r => r.BlksHit).ToArray(),
            Read = rows.Select(r => r.BlksRead).ToArray(),
            Temp = rows.Select(r => r.TempWritten).ToArray(),
            Mean = rows.Select(r => r.MeanMs).ToArray(),
            Stddev = rows.Select(r => r.StddevMs).ToArray(),
            Max = rows.Select(r => r.MaxMs).ToArray(),
        }, cancellationToken: ct));

        // Query text is stored redacted (literals → ?); EXPLAIN reads the
        // live text from pg_stat_statements instead.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO perf_pg_query_text (queryid, query, last_seen_utc)
            SELECT * FROM unnest(@Ids, @Texts, @Seen)
            ON CONFLICT (queryid) DO UPDATE SET last_seen_utc = EXCLUDED.last_seen_utc
            """, new
        {
            Ids = rows.Select(r => r.QueryId).ToArray(),
            Texts = rows.Select(r => PerfRedactor.Sql(r.Query)).ToArray(),
            Seen = rows.Select(_ => ts.UtcDateTime).ToArray(),
        }, cancellationToken: ct));
    }

    public async Task SnapshotDatabaseAsync(DateTimeOffset ts, CancellationToken ct)
    {
        var payload = await ReadDatabaseStatsAsync(ct);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO perf_pg_db_snapshot (ts_utc, payload) VALUES (@Ts, CAST(@Payload AS jsonb))
            ON CONFLICT (ts_utc) DO NOTHING
            """, new { Ts = ts.UtcDateTime, Payload = JsonSerializer.Serialize(payload) }, cancellationToken: ct));
    }

    /// Cumulative database counters + checkpoint stats + an activity summary.
    public async Task<Dictionary<string, double?>> ReadDatabaseStatsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var result = new Dictionary<string, double?>();

        var db = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT d.blks_hit::float8 AS blks_hit, d.blks_read::float8 AS blks_read,
                   d.temp_files::float8 AS temp_files, d.temp_bytes::float8 AS temp_bytes,
                   d.deadlocks::float8 AS deadlocks, d.xact_commit::float8 AS xact_commit,
                   d.xact_rollback::float8 AS xact_rollback, d.conflicts::float8 AS conflicts,
                   d.tup_returned::float8 AS tup_returned, d.tup_fetched::float8 AS tup_fetched,
                   d.tup_inserted::float8 AS tup_inserted, d.tup_updated::float8 AS tup_updated,
                   d.tup_deleted::float8 AS tup_deleted, d.numbackends::float8 AS numbackends,
                   EXTRACT(EPOCH FROM d.stats_reset)::float8 AS stats_reset_epoch,
                   pg_database_size(current_database())::float8 AS db_size,
                   (SELECT age(datfrozenxid) FROM pg_database WHERE datname = current_database())::float8 AS xid_age,
                   current_setting('max_connections')::float8 AS max_connections
            FROM pg_stat_database d
            WHERE d.datname = current_database()
            """, cancellationToken: ct));
        if (db is IDictionary<string, object?> dbMap) Merge(result, dbMap);

        var version = Access.ServerVersionNum;
        try
        {
            var cp = version >= 170000
                ? await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
                    SELECT num_timed::float8 AS checkpoints_timed, num_requested::float8 AS checkpoints_req,
                           buffers_written::float8 AS buffers_checkpoint,
                           (SELECT buffers_clean::float8 FROM pg_stat_bgwriter) AS buffers_clean,
                           (SELECT maxwritten_clean::float8 FROM pg_stat_bgwriter) AS maxwritten_clean
                    FROM pg_stat_checkpointer
                    """, cancellationToken: ct))
                : await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
                    SELECT checkpoints_timed::float8 AS checkpoints_timed, checkpoints_req::float8 AS checkpoints_req,
                           buffers_checkpoint::float8 AS buffers_checkpoint, buffers_clean::float8 AS buffers_clean,
                           maxwritten_clean::float8 AS maxwritten_clean, buffers_backend::float8 AS buffers_backend
                    FROM pg_stat_bgwriter
                    """, cancellationToken: ct));
            if (cp is IDictionary<string, object?> cpMap) Merge(result, cpMap);
        }
        catch (PostgresException)
        {
            // view shape differs on this server version — skip
        }

        var act = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT count(*)::float8 AS conn_total,
                   count(*) FILTER (WHERE state = 'active')::float8 AS conn_active,
                   count(*) FILTER (WHERE state = 'idle')::float8 AS conn_idle,
                   count(*) FILTER (WHERE state LIKE 'idle in transaction%')::float8 AS idle_in_tx,
                   COALESCE(max(EXTRACT(EPOCH FROM now() - state_change))
                            FILTER (WHERE state LIKE 'idle in transaction%'), 0)::float8 AS idle_in_tx_max_s,
                   COALESCE(max(EXTRACT(EPOCH FROM now() - query_start))
                            FILTER (WHERE state = 'active' AND backend_type = 'client backend'), 0)::float8 AS longest_active_s,
                   count(*) FILTER (WHERE wait_event_type = 'Lock')::float8 AS waiting_on_lock,
                   count(*) FILTER (WHERE cardinality(pg_blocking_pids(pid)) > 0)::float8 AS blocked
            FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid()
            """, cancellationToken: ct));
        if (act is IDictionary<string, object?> actMap) Merge(result, actMap);
        return result;
    }

    private static void Merge(Dictionary<string, double?> target, IDictionary<string, object?> source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = value switch
            {
                null => null,
                double d => d,
                float f => f,
                decimal m => (double)m,
                long l => l,
                int i => i,
                _ => double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null,
            };
        }
    }

    public async Task SnapshotTablesAsync(DateTimeOffset ts, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO perf_pg_table_snapshot (ts_utc, relname, n_live, n_dead, seq_scan, seq_tup_read, idx_scan,
                n_ins, n_upd, n_del, n_hot_upd, mod_since_analyze, heap_read, heap_hit, idx_read, idx_hit,
                total_bytes, table_bytes, index_bytes, last_vacuum, last_autovacuum, last_analyze, last_autoanalyze)
            SELECT @Ts, t.relname, t.n_live_tup, t.n_dead_tup, COALESCE(t.seq_scan, 0), COALESCE(t.seq_tup_read, 0),
                   COALESCE(t.idx_scan, 0), t.n_tup_ins, t.n_tup_upd, t.n_tup_del, t.n_tup_hot_upd, t.n_mod_since_analyze,
                   COALESCE(io.heap_blks_read, 0), COALESCE(io.heap_blks_hit, 0),
                   COALESCE(io.idx_blks_read, 0), COALESCE(io.idx_blks_hit, 0),
                   pg_total_relation_size(t.relid), pg_relation_size(t.relid), pg_indexes_size(t.relid),
                   t.last_vacuum, t.last_autovacuum, t.last_analyze, t.last_autoanalyze
            FROM pg_stat_user_tables t
            LEFT JOIN pg_statio_user_tables io ON io.relid = t.relid
            WHERE t.schemaname = 'public'
            """, new { Ts = ts.UtcDateTime }, cancellationToken: ct));
    }

    // ----------------------------------------------------------------- live

    public sealed class TableRow
    {
        public string Name { get; set; } = "";
        public long Rows { get; set; }
        public long Dead { get; set; }
        public long SeqScan { get; set; }
        public long SeqTupRead { get; set; }
        public long IdxScan { get; set; }
        public long ModSinceAnalyze { get; set; }
        public long Inserts { get; set; }
        public long Updates { get; set; }
        public long Deletes { get; set; }
        public long HotUpdates { get; set; }
        public long HeapRead { get; set; }
        public long HeapHit { get; set; }
        public long IdxRead { get; set; }
        public long IdxHit { get; set; }
        public long TotalBytes { get; set; }
        public long TableBytes { get; set; }
        public long IndexBytes { get; set; }
        public DateTime? LastVacuum { get; set; }
        public DateTime? LastAutovacuum { get; set; }
        public DateTime? LastAnalyze { get; set; }
        public DateTime? LastAutoanalyze { get; set; }
    }

    public async Task<IReadOnlyList<TableRow>> ReadTablesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TableRow>(new CommandDefinition("""
            SELECT t.relname AS Name, t.n_live_tup AS Rows, t.n_dead_tup AS Dead,
                   COALESCE(t.seq_scan, 0) AS SeqScan, COALESCE(t.seq_tup_read, 0) AS SeqTupRead,
                   COALESCE(t.idx_scan, 0) AS IdxScan, t.n_mod_since_analyze AS ModSinceAnalyze,
                   t.n_tup_ins AS Inserts, t.n_tup_upd AS Updates, t.n_tup_del AS Deletes, t.n_tup_hot_upd AS HotUpdates,
                   COALESCE(io.heap_blks_read, 0) AS HeapRead, COALESCE(io.heap_blks_hit, 0) AS HeapHit,
                   COALESCE(io.idx_blks_read, 0) AS IdxRead, COALESCE(io.idx_blks_hit, 0) AS IdxHit,
                   (sz.heap + sz.toast + sz.idx) AS TotalBytes, sz.heap AS TableBytes, sz.idx AS IndexBytes,
                   t.last_vacuum AS LastVacuum, t.last_autovacuum AS LastAutovacuum,
                   t.last_analyze AS LastAnalyze, t.last_autoanalyze AS LastAutoanalyze
            FROM pg_stat_user_tables t
            LEFT JOIN pg_statio_user_tables io ON io.relid = t.relid
            CROSS JOIN LATERAL (
                -- Catalog page counts (kept current by vacuum/analyze) instead of
                -- pg_*_size(), which stats every file of every relation and is
                -- far too slow for a dashboard read on a schema this size.
                SELECT c.relpages::bigint * bs.v AS heap,
                       COALESCE(toast.relpages, 0)::bigint * bs.v AS toast,
                       COALESCE((SELECT sum(ic.relpages) FROM pg_index ix JOIN pg_class ic ON ic.oid = ix.indexrelid
                                 WHERE ix.indrelid = c.oid), 0)::bigint * bs.v AS idx
                FROM pg_class c
                CROSS JOIN (SELECT current_setting('block_size')::bigint AS v) bs
                LEFT JOIN pg_class toast ON toast.oid = c.reltoastrelid
                WHERE c.oid = t.relid
            ) sz
            WHERE t.schemaname = 'public'
            ORDER BY TotalBytes DESC
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public sealed class BloatRow
    {
        public string Name { get; set; } = "";
        public double RealBytes { get; set; }
        public double BloatBytes { get; set; }
        public double BloatPct { get; set; }
    }

    /// Statistical table-bloat estimate (expected pages from pg_stats row
    /// widths vs actual pages) — the well-known estimation approach, no
    /// extension or table scan needed. Approximate by nature; small tables
    /// and freshly-analysed ones are the least accurate.
    public async Task<IReadOnlyList<BloatRow>> ReadBloatAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BloatRow>(new CommandDefinition("""
            SELECT tblname AS Name,
                   (bs * tblpages)::float8 AS RealBytes,
                   GREATEST((tblpages - est_tblpages_ff) * bs, 0)::float8 AS BloatBytes,
                   CASE WHEN tblpages > 0 AND tblpages - est_tblpages_ff > 0
                        THEN 100 * (tblpages - est_tblpages_ff) / tblpages::float8 ELSE 0 END AS BloatPct
            FROM (
                SELECT ceil(reltuples / ((bs - page_hdr) * fillfactor / (tpl_size * 100))) + ceil(toasttuples / 4) AS est_tblpages_ff,
                       tblpages, bs, tblname, is_na
                FROM (
                    SELECT (4 + tpl_hdr_size + tpl_data_size + (2 * ma)
                            - CASE WHEN tpl_hdr_size % ma = 0 THEN ma ELSE tpl_hdr_size % ma END
                            - CASE WHEN ceil(tpl_data_size)::int % ma = 0 THEN ma ELSE ceil(tpl_data_size)::int % ma END
                           ) AS tpl_size,
                           (heappages + toastpages) AS tblpages, reltuples, toasttuples, bs, page_hdr,
                           tblname, fillfactor, is_na
                    FROM (
                        SELECT tbl.relname AS tblname, tbl.reltuples,
                               tbl.relpages AS heappages, COALESCE(toast.relpages, 0) AS toastpages,
                               COALESCE(toast.reltuples, 0) AS toasttuples,
                               COALESCE(substring(array_to_string(tbl.reloptions, ' ') FROM 'fillfactor=([0-9]+)')::smallint, 100) AS fillfactor,
                               current_setting('block_size')::numeric AS bs,
                               8 AS ma,
                               24 AS page_hdr,
                               23 + CASE WHEN max(COALESCE(s.null_frac, 0)) > 0 THEN (7 + count(s.attname)) / 8 ELSE 0::int END AS tpl_hdr_size,
                               sum((1 - COALESCE(s.null_frac, 0)) * COALESCE(s.avg_width, 0)) AS tpl_data_size,
                               bool_or(att.atttypid = 'pg_catalog.name'::regtype)
                                   OR sum(CASE WHEN att.attnum > 0 THEN 1 ELSE 0 END) <> count(s.attname) AS is_na
                        FROM pg_attribute att
                        JOIN pg_class tbl ON att.attrelid = tbl.oid
                        JOIN pg_namespace ns ON ns.oid = tbl.relnamespace
                        LEFT JOIN pg_stats s ON s.schemaname = ns.nspname AND s.tablename = tbl.relname
                                            AND s.inherited = false AND s.attname = att.attname
                        LEFT JOIN pg_class toast ON tbl.reltoastrelid = toast.oid
                        WHERE NOT att.attisdropped AND att.attnum > 0
                          AND tbl.relkind IN ('r', 'm') AND ns.nspname = 'public'
                        GROUP BY tbl.relname, tbl.reltuples, tbl.relpages, toast.relpages, toast.reltuples, tbl.reloptions
                    ) a
                ) b
            ) c
            WHERE NOT is_na
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public sealed class IndexRow
    {
        public string Table { get; set; } = "";
        public string Index { get; set; } = "";
        public long Scans { get; set; }
        public long Bytes { get; set; }
        public bool IsUnique { get; set; }
        public bool IsPrimary { get; set; }
    }

    public async Task<IReadOnlyList<IndexRow>> ReadIndexesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<IndexRow>(new CommandDefinition("""
            SELECT s.relname AS Table, s.indexrelname AS Index, s.idx_scan AS Scans,
                   ic.relpages::bigint * current_setting('block_size')::bigint AS Bytes,
                   i.indisunique AS IsUnique, i.indisprimary AS IsPrimary
            FROM pg_stat_user_indexes s
            JOIN pg_index i ON i.indexrelid = s.indexrelid
            JOIN pg_class ic ON ic.oid = s.indexrelid
            WHERE s.schemaname = 'public'
            ORDER BY ic.relpages DESC
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public sealed class DuplicateIndexRow
    {
        public string Table { get; set; } = "";
        public string[] Indexes { get; set; } = Array.Empty<string>();
        public long Bytes { get; set; }
    }

    /// Indexes on the same table with an identical key, expression and predicate.
    public async Task<IReadOnlyList<DuplicateIndexRow>> ReadDuplicateIndexesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DuplicateIndexRow>(new CommandDefinition("""
            SELECT c.relname AS Table,
                   array_agg(ic.relname ORDER BY ic.relname) AS Indexes,
                   (sum(ic.relpages) * current_setting('block_size')::bigint)::bigint AS Bytes
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
            GROUP BY c.relname, i.indkey::text, COALESCE(i.indexprs::text, ''), COALESCE(i.indpred::text, ''), i.indclass::text
            HAVING count(*) > 1
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public sealed class ActivityRow
    {
        public int Pid { get; set; }
        public string? State { get; set; }
        public string? WaitEventType { get; set; }
        public string? WaitEvent { get; set; }
        public string? BackendType { get; set; }
        public string? Application { get; set; }
        public double? QueryAgeS { get; set; }
        public double? XactAgeS { get; set; }
        public double? StateAgeS { get; set; }
        public int[] BlockedBy { get; set; } = Array.Empty<int>();
        public string? Query { get; set; }
    }

    public async Task<IReadOnlyList<ActivityRow>> ReadActivityAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ActivityRow>(new CommandDefinition("""
            SELECT pid AS Pid, state AS State, wait_event_type AS WaitEventType, wait_event AS WaitEvent,
                   backend_type AS BackendType, left(application_name, 60) AS Application,
                   EXTRACT(EPOCH FROM now() - query_start)::float8 AS QueryAgeS,
                   EXTRACT(EPOCH FROM now() - xact_start)::float8 AS XactAgeS,
                   EXTRACT(EPOCH FROM now() - state_change)::float8 AS StateAgeS,
                   pg_blocking_pids(pid) AS BlockedBy,
                   left(query, 4000) AS Query
            FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid()
            ORDER BY (state = 'active') DESC, query_start NULLS LAST
            LIMIT 200
            """, cancellationToken: ct))).AsList();
        foreach (var r in rows) r.Query = PerfRedactor.Sql(r.Query);
        return rows;
    }

    public sealed class LockRow
    {
        public int Pid { get; set; }
        public string LockType { get; set; } = "";
        public string Mode { get; set; } = "";
        public bool Granted { get; set; }
        public string? Relation { get; set; }
        public double? WaitingS { get; set; }
    }

    public async Task<IReadOnlyList<LockRow>> ReadLocksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<LockRow>(new CommandDefinition("""
            SELECT l.pid AS Pid, l.locktype AS LockType, l.mode AS Mode, l.granted AS Granted,
                   c.relname AS Relation,
                   EXTRACT(EPOCH FROM now() - l.waitstart)::float8 AS WaitingS
            FROM pg_locks l
            LEFT JOIN pg_class c ON c.oid = l.relation
            WHERE l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND (NOT l.granted OR l.pid IN (
                    SELECT unnest(pg_blocking_pids(a.pid)) FROM pg_stat_activity a
                    WHERE a.datname = current_database()))
            ORDER BY l.granted, l.pid
            LIMIT 200
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public sealed class SettingRow
    {
        public string Name { get; set; } = "";
        public string Setting { get; set; } = "";
        public string? Unit { get; set; }
        public string? Source { get; set; }
    }

    public static readonly string[] InterestingSettings =
    {
        "shared_buffers", "effective_cache_size", "work_mem", "maintenance_work_mem", "max_connections",
        "random_page_cost", "effective_io_concurrency", "max_wal_size", "checkpoint_timeout",
        "autovacuum", "autovacuum_naptime", "autovacuum_vacuum_scale_factor", "autovacuum_analyze_scale_factor",
        "autovacuum_vacuum_cost_limit", "autovacuum_max_workers", "default_statistics_target",
        "log_min_duration_statement", "track_io_timing", "jit", "shared_preload_libraries",
        "pg_stat_statements.track", "idle_in_transaction_session_timeout", "statement_timeout",
    };

    public async Task<IReadOnlyList<SettingRow>> ReadSettingsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SettingRow>(new CommandDefinition("""
            SELECT name AS Name, setting AS Setting, unit AS Unit, source AS Source
            FROM pg_settings WHERE name = ANY(@Names) ORDER BY name
            """, new { Names = InterestingSettings }, cancellationToken: ct));
        return rows.AsList();
    }

    public sealed class IoRow
    {
        public string BackendType { get; set; } = "";
        public string Object { get; set; } = "";
        public string Context { get; set; } = "";
        public long? Reads { get; set; }
        public double? ReadTime { get; set; }
        public long? Writes { get; set; }
        public double? WriteTime { get; set; }
        public long? Hits { get; set; }
        public long? Evictions { get; set; }
        public long? Fsyncs { get; set; }
    }

    /// pg_stat_io (PostgreSQL 16+); empty on older servers.
    public async Task<IReadOnlyList<IoRow>> ReadIoAsync(CancellationToken ct)
    {
        if (Access.ServerVersionNum is > 0 and < 160000) return Array.Empty<IoRow>();
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await conn.QueryAsync<IoRow>(new CommandDefinition("""
                SELECT backend_type AS BackendType, object AS Object, context AS Context,
                       reads AS Reads, read_time AS ReadTime, writes AS Writes, write_time AS WriteTime,
                       hits AS Hits, evictions AS Evictions, fsyncs AS Fsyncs
                FROM pg_stat_io
                WHERE COALESCE(reads, 0) + COALESCE(writes, 0) + COALESCE(hits, 0) > 0
                ORDER BY COALESCE(reads, 0) + COALESCE(writes, 0) DESC
                LIMIT 40
                """, cancellationToken: ct));
            return rows.AsList();
        }
        catch (PostgresException)
        {
            return Array.Empty<IoRow>();
        }
    }
}
