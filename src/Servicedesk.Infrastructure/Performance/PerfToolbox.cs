using System.Data;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Performance;

public sealed record PlanResult(bool Ok, string? PlanJson, string? Error, string? Sql);

public sealed record AnalyzeResult(bool Ok, string? Error);

/// Admin-triggered actions on the Performance page. All of them are
/// explicit, audited by the endpoint, and bounded:
/// <list type="bullet">
/// <item><b>Plan</b> — EXPLAIN (GENERIC_PLAN) on a stored query shape. Never
///   EXPLAIN ANALYZE: the statement is planned, not executed. SELECT/WITH
///   only (no data-modifying CTEs), single statement, read-only transaction,
///   statement_timeout. The SQL text always comes from the server's own
///   stores — the client sends only an id.</item>
/// <item><b>Analyze</b> — ANALYZE on one table that exists in the app schema
///   (identifier quoted by PostgreSQL's own format('%I')).</item>
/// <item><b>Benchmark</b> — a ~1 s CPU loop, a small fsync'd disk write in the
///   blob root and Postgres round trips; never automatic.</item>
/// <item><b>Edge check</b> — fetches the public URL once and reports the
///   HTTP version, compression and cache headers nginx returns.</item>
/// </list>
/// No VACUUM FULL, REINDEX or configuration change is ever issued.
public sealed partial class PerfToolbox
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PerfToolbox> _logger;

    public PerfToolbox(NpgsqlDataSource dataSource, ISettingsService settings, IHttpClientFactory http, ILogger<PerfToolbox> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _http = http;
        _logger = logger;
    }

    // ------------------------------------------------------------------ plan

    public async Task<PlanResult> ExplainAsync(string source, string id, CancellationToken ct)
    {
        string? sql;
        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        {
            if (source == "pgss")
            {
                if (!long.TryParse(id, out var queryId)) return new PlanResult(false, null, "Invalid query id.", null);
                sql = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
                    SELECT query FROM pg_stat_statements
                    WHERE queryid = @Id AND dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                    LIMIT 1
                    """, new { Id = queryId }, cancellationToken: ct));
            }
            else if (source == "app")
            {
                if (!FingerprintId().IsMatch(id)) return new PlanResult(false, null, "Invalid fingerprint.", null);
                var stored = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT sql_text FROM perf_sql_fingerprint WHERE fingerprint = @Id", new { Id = id }, cancellationToken: ct));
                sql = stored is null ? null : SqlFingerprint.ToPositional(stored);
            }
            else
            {
                return new PlanResult(false, null, "Unknown source.", null);
            }
        }

        // A shape that cannot be planned generically (untyped $n, …) fails
        // in PostgreSQL by design; don't count those attempts as errors.
        PerfContext.ExpectedFailuresOnly = true;

        if (string.IsNullOrWhiteSpace(sql)) return new PlanResult(false, null, "Query text not found (statistics reset or not captured yet).", null);
        var check = ValidateForPlan(sql);
        if (check is not null) return new PlanResult(false, null, check, PerfRedactor.Sql(sql));

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            // The statement contains unbound $n placeholders. Sent directly,
            // the driver's extended protocol would demand values for them;
            // run through PL/pgSQL EXECUTE instead (no bind step), from a
            // session-temporary helper that disappears with the session reset.
            await conn.ExecuteAsync(new CommandDefinition("""
                CREATE OR REPLACE FUNCTION pg_temp.sd_perf_explain(q text) RETURNS text
                LANGUAGE plpgsql AS $fn$
                DECLARE plan text;
                BEGIN
                    EXECUTE 'EXPLAIN (GENERIC_PLAN, FORMAT JSON) ' || q INTO plan;
                    RETURN plan;
                END
                $fn$
                """, cancellationToken: ct));
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '1s';",
                transaction: tx, cancellationToken: ct));
            var plan = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT pg_temp.sd_perf_explain(@Sql)", new { Sql = sql.TrimEnd().TrimEnd(';') },
                transaction: tx, cancellationToken: ct));
            await tx.RollbackAsync(ct);
            return new PlanResult(true, plan, null, PerfRedactor.Sql(sql));
        }
        catch (PostgresException ex)
        {
            var message = ex.SqlState == "42601" && ex.MessageText.Contains("GENERIC_PLAN", StringComparison.OrdinalIgnoreCase)
                ? "EXPLAIN (GENERIC_PLAN) needs PostgreSQL 16 or newer."
                : $"PostgreSQL could not plan this statement ({ex.SqlState}): {ex.MessageText}";
            return new PlanResult(false, null, PerfRedactor.Text(message), PerfRedactor.Sql(sql));
        }
    }

    /// Only plain reads may be planned: SELECT, or WITH … SELECT without a
    /// data-modifying CTE; a single statement.
    internal static string? ValidateForPlan(string sql)
    {
        var trimmed = StripComments().Replace(sql, " ").Trim().TrimEnd(';').Trim();
        if (trimmed.Contains(';')) return "Only a single statement can be planned.";
        var head = trimmed.Length > 10 ? trimmed[..10].ToUpperInvariant() : trimmed.ToUpperInvariant();
        if (head.StartsWith("SELECT", StringComparison.Ordinal)) return ModifyingKeyword().IsMatch(trimmed) ? "Only read-only statements can be planned." : null;
        if (head.StartsWith("WITH", StringComparison.Ordinal))
            return ModifyingKeyword().IsMatch(trimmed) ? "Statements with data-modifying CTEs cannot be planned." : null;
        if (head.StartsWith("(", StringComparison.Ordinal) && !ModifyingKeyword().IsMatch(trimmed)) return null;
        return "Only SELECT statements can be planned.";
    }

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|DROP|ALTER|CREATE|GRANT|REVOKE|COPY|CALL|DO|VACUUM|LOCK)\b|\bFOR\s+(UPDATE|SHARE|NO\s+KEY\s+UPDATE|KEY\s+SHARE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModifyingKeyword();

    [GeneratedRegex(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline)]
    private static partial Regex StripComments();

    [GeneratedRegex(@"^[0-9a-f]{12}$")]
    private static partial Regex FingerprintId();

    // --------------------------------------------------------------- analyze

    public async Task<AnalyzeResult> AnalyzeAsync(string table, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(table) || table.Length > 63) return new AnalyzeResult(false, "Invalid table name.");
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // The statement text is produced by PostgreSQL itself from a table
        // that exists in the catalog — the caller's string is only a lookup key.
        var statement = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT format('ANALYZE public.%I', relname)
            FROM pg_stat_user_tables WHERE schemaname = 'public' AND relname = @Table
            """, new { Table = table }, cancellationToken: ct));
        if (statement is null) return new AnalyzeResult(false, "Unknown table.");
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("SET statement_timeout = '120s'", cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(statement, commandTimeout: 130, cancellationToken: ct));
            return new AnalyzeResult(true, null);
        }
        catch (PostgresException ex)
        {
            return new AnalyzeResult(false, $"ANALYZE failed ({ex.SqlState}).");
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition("RESET statement_timeout", cancellationToken: CancellationToken.None));
        }
    }

    // ------------------------------------------------------------- benchmark

    public async Task<object> BenchmarkAsync(string createdBy, CancellationToken ct)
    {
        // CPU: SHA-256 over a fixed buffer for ~1 s.
        var buffer = new byte[64 * 1024];
        RandomNumberGenerator.Fill(buffer);
        var sw = Stopwatch.StartNew();
        long hashes = 0;
        while (sw.ElapsedMilliseconds < 1000)
        {
            SHA256.HashData(buffer);
            hashes++;
        }
        var cpuMbPerSec = hashes * buffer.Length / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;

        // Disk: 4 MB in 64 KB chunks, fsync after each chunk, in the blob root.
        double? fsyncMs = null, writeMbPerSec = null;
        string? diskError = null;
        try
        {
            var root = await _settings.GetAsync<string>(SettingKeys.Storage.BlobRoot, ct);
            var dir = Directory.Exists(root) ? root : Path.GetTempPath();
            var path = Path.Combine(dir, ".perf-bench-" + Guid.NewGuid().ToString("N"));
            try
            {
                var times = new List<double>();
                var total = Stopwatch.StartNew();
                await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    for (var i = 0; i < 64; i++)
                    {
                        var one = Stopwatch.StartNew();
                        await fs.WriteAsync(buffer, ct);
                        fs.Flush(flushToDisk: true);
                        times.Add(one.Elapsed.TotalMilliseconds);
                    }
                }
                total.Stop();
                fsyncMs = times.Average();
                writeMbPerSec = 4.0 / total.Elapsed.TotalSeconds;
            }
            finally
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diskError = ex.GetType().Name;
        }

        // Postgres: 50 × SELECT 1 on one connection (pure round-trip latency).
        var rtts = new List<double>();
        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        {
            for (var i = 0; i < 50; i++)
            {
                var one = Stopwatch.StartNew();
                await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
                rtts.Add(one.Elapsed.TotalMilliseconds);
            }
        }
        rtts.Sort();

        var result = new
        {
            cpuSha256MbPerSec = Math.Round(cpuMbPerSec, 1),
            cores = Environment.ProcessorCount,
            diskFsyncMsAvg = fsyncMs is null ? (double?)null : Math.Round(fsyncMs.Value, 2),
            diskWriteMbPerSec = writeMbPerSec is null ? (double?)null : Math.Round(writeMbPerSec.Value, 1),
            diskError,
            pgRoundTripMsMedian = Math.Round(rtts[rtts.Count / 2], 3),
            pgRoundTripMsP95 = Math.Round(rtts[(int)(rtts.Count * 0.95)], 3),
            appVersion = PerfAppInfo.Version,
        };

        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO perf_benchmark (payload, created_by) VALUES (CAST(@Payload AS jsonb), @CreatedBy)
                """, new { Payload = JsonSerializer.Serialize(result), CreatedBy = createdBy }, cancellationToken: ct));
        }
        return result;
    }

    public sealed class BenchmarkRow
    {
        public long Id { get; set; }
        public DateTime TsUtc { get; set; }
        public string Payload { get; set; } = "";
        public string CreatedBy { get; set; } = "";
    }

    public async Task<IReadOnlyList<BenchmarkRow>> BenchmarksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BenchmarkRow>(new CommandDefinition("""
            SELECT id AS Id, ts_utc AS TsUtc, payload::text AS Payload, created_by AS CreatedBy
            FROM perf_benchmark ORDER BY ts_utc DESC LIMIT 20
            """, cancellationToken: ct));
        return rows.AsList();
    }

    // ------------------------------------------------------------ edge check

    public sealed record EdgeCheckItem(string Label, string Value, string Status, string Hint);

    public async Task<IReadOnlyList<EdgeCheckItem>> EdgeCheckAsync(CancellationToken ct)
    {
        var items = new List<EdgeCheckItem>();
        var baseUrl = await _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp))
        {
            items.Add(new EdgeCheckItem("Public URL", "not configured", "warning", "Set App.PublicBaseUrl (Settings → General) so the check knows where nginx listens."));
            return items;
        }

        using var client = _http.CreateClient("perf-edge-check");
        client.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "/"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
            var sw = Stopwatch.StartNew();
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var ttfb = sw.Elapsed.TotalMilliseconds;
            items.Add(new EdgeCheckItem("Response", $"{(int)resp.StatusCode} in {ttfb:0} ms (TTFB from the server itself)", resp.IsSuccessStatusCode ? "good" : "warning",
                "Measured from inside the app container through nginx — a baseline without the user's internet connection."));
            items.Add(new EdgeCheckItem("HTTP version", "HTTP/" + resp.Version, resp.Version >= HttpVersion.Version20 ? "good" : "warning",
                "HTTP/2 multiplexes the many parallel API calls of the SPA over one connection."));
            var encoding = string.Join(",", resp.Content.Headers.ContentEncoding);
            items.Add(new EdgeCheckItem("Compression (index.html)", encoding.Length == 0 ? "none" : encoding, encoding.Length == 0 ? "info" : "good",
                "gzip/brotli shrinks HTML/JSON/JS considerably; small responses are left uncompressed by design."));
            items.Add(new EdgeCheckItem("HSTS", resp.Headers.Contains("Strict-Transport-Security") ? "present" : "missing",
                resp.Headers.Contains("Strict-Transport-Security") || root.Scheme == Uri.UriSchemeHttp ? "good" : "warning", "Set by nginx on HTTPS installs."));

            var html = await resp.Content.ReadAsStringAsync(ct);
            var asset = AssetRef().Match(html);
            if (asset.Success)
            {
                using var assetReq = new HttpRequestMessage(HttpMethod.Get, new Uri(root, asset.Groups[1].Value))
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                assetReq.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
                using var assetResp = await client.SendAsync(assetReq, HttpCompletionOption.ResponseHeadersRead, ct);
                var cache = assetResp.Headers.CacheControl?.ToString() ?? "";
                items.Add(new EdgeCheckItem("Cache-Control (hashed asset)", cache.Length == 0 ? "none" : cache,
                    cache.Contains("immutable", StringComparison.OrdinalIgnoreCase) ? "good" : "warning",
                    "Content-hashed Vite assets should be cached for a year and marked immutable."));
                var assetEncoding = string.Join(",", assetResp.Content.Headers.ContentEncoding);
                items.Add(new EdgeCheckItem("Compression (JavaScript)", assetEncoding.Length == 0 ? "none" : assetEncoding,
                    assetEncoding.Length == 0 ? "warning" : "good", "JavaScript bundles should always be served compressed."));
            }
            items.Add(new EdgeCheckItem("Keep-alive", resp.Headers.ConnectionClose == true ? "closed" : "kept open",
                resp.Headers.ConnectionClose == true ? "warning" : "good", "Re-using connections avoids a TCP/TLS handshake per request."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Edge check failed.");
            items.Add(new EdgeCheckItem("Reachability", "failed: " + ex.GetType().Name, "warning",
                "The server could not reach its own public URL (hairpin NAT / DNS inside the container). Check from a browser's devtools instead."));
        }
        return items;
    }

    [GeneratedRegex("""<script[^>]+src="(/assets/[^"]+\.js)""", RegexOptions.IgnoreCase)]
    private static partial Regex AssetRef();

    // ------------------------------------------------------------ clear data

    private static readonly string[] DataTables =
    {
        "perf_http_minute", "perf_http_hour", "perf_db_query_minute", "perf_db_query_hour", "perf_sql_fingerprint",
        "perf_span_minute", "perf_span_hour", "perf_rum_minute", "perf_rum_hour", "perf_nplusone_minute",
        "perf_slow_request", "perf_worker_run", "perf_runtime_minute", "perf_pg_statement_snapshot",
        "perf_pg_query_text", "perf_pg_db_snapshot", "perf_pg_table_snapshot", "perf_rollup_state", "perf_alert_state",
    };

    /// Empties every collected metric (markers and benchmarks are kept).
    public async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("TRUNCATE " + string.Join(", ", DataTables), cancellationToken: ct));
    }
}
