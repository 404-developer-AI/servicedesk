using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Storage;

/// Mark-and-sweep GC for the content-addressed blob store. Blob files are
/// shared (dedup) and rows never delete their file, so the only safe way to
/// reclaim space is: mark every hash any row still references, then sweep
/// files that are unreferenced AND untouched for the grace period
/// (<c>Storage.OrphanRetentionHours</c>). The grace period is what makes the
/// write order safe — callers write the blob first and its row afterwards.
///
/// Before deleting, each batch of candidates is re-checked against the
/// database, and the delete itself re-checks the file's mtime under the blob
/// store's per-hash lock (a dedup hit refreshes the mtime), so a row created
/// mid-run never ends up pointing at a deleted file.
///
/// Also applies <c>Storage.RawEmlRetentionDays</c> (drops the reference; the
/// sweep then reclaims the file) and clears stale <c>.tmp</c> staging files.
/// <c>Storage.BlobGcDryRun</c> (default on) only counts — nothing is changed.
///
/// Every place that stores a blob hash must be listed in
/// <see cref="ReferencedSql"/> / <see cref="StillReferencedSql"/>; a new
/// blob-owning column that is missing here gets its files deleted.
public sealed class BlobGarbageCollector : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(24);
    private const int MinGraceHours = 6;
    private const int RecheckBatchSize = 2000;
    private const int RawEmlBatchSize = 1000;

    // Every column that references a blob. Settings are matched by shape
    // (Signatures.PhotoFrameBlobHash today) so a future hash-valued setting
    // is protected without a code change here.
    internal const string ReferencedSql = """
        SELECT content_hash FROM attachments WHERE content_hash IS NOT NULL
        UNION ALL SELECT raw_eml_blob_hash FROM mail_messages WHERE raw_eml_blob_hash IS NOT NULL
        UNION ALL SELECT body_html_blob_hash FROM mail_messages WHERE body_html_blob_hash IS NOT NULL
        UNION ALL SELECT content_hash FROM signature_assets
        UNION ALL SELECT photo_blob_hash FROM users WHERE photo_blob_hash IS NOT NULL
        UNION ALL SELECT value FROM settings WHERE value ~ '^[0-9a-f]{64}$'
        """;

    internal const string StillReferencedSql = """
        SELECT content_hash FROM attachments WHERE content_hash = ANY(@Hashes)
        UNION SELECT raw_eml_blob_hash FROM mail_messages WHERE raw_eml_blob_hash = ANY(@Hashes)
        UNION SELECT body_html_blob_hash FROM mail_messages WHERE body_html_blob_hash = ANY(@Hashes)
        UNION SELECT content_hash FROM signature_assets WHERE content_hash = ANY(@Hashes)
        UNION SELECT photo_blob_hash FROM users WHERE photo_blob_hash = ANY(@Hashes)
        UNION SELECT value FROM settings WHERE value = ANY(@Hashes)
        """;

    private const string RawEmlCountSql = """
        SELECT count(*) FROM mail_messages
         WHERE raw_eml_blob_hash IS NOT NULL AND received_utc < @Cutoff
        """;

    private const string RawEmlExpireSql = """
        UPDATE mail_messages SET raw_eml_blob_hash = NULL
         WHERE id IN (
            SELECT id FROM mail_messages
             WHERE raw_eml_blob_hash IS NOT NULL AND received_utc < @Cutoff
             ORDER BY id
             LIMIT @BatchSize)
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IBlobStoreMaintenance _store;
    private readonly ISettingsService _settings;
    private readonly IBlobGcHealth _health;
    private readonly IAuditLogger _audit;
    private readonly ILogger<BlobGarbageCollector> _logger;

    public BlobGarbageCollector(
        NpgsqlDataSource dataSource,
        IBlobStoreMaintenance store,
        ISettingsService settings,
        IBlobGcHealth health,
        IAuditLogger audit,
        ILogger<BlobGarbageCollector> logger)
    {
        _dataSource = dataSource;
        _store = store;
        _settings = settings;
        _health = health;
        _audit = audit;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _health.WaitForRunRequestAsync(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(24);
            using (var run = PerfWorkerRun.Start("blob-gc"))
            {
                try
                {
                    interval = TimeSpan.FromHours(Math.Clamp(
                        await _settings.GetAsync<int>(SettingKeys.Storage.BlobGcIntervalHours, stoppingToken), 1, 168));
                    var snapshot = await RunOnceAsync(stoppingToken);
                    run.AddItems(snapshot.DeletedFiles);
                    _health.RecordRun(snapshot, DateTime.UtcNow + interval);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "Blob GC run failed.");
                    _health.RecordFailure(ex.Message, DateTime.UtcNow + interval);
                }
            }

            try { await _health.WaitForRunRequestAsync(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task<BlobGcSnapshot> RunOnceAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dryRun = await _settings.GetAsync<bool>(SettingKeys.Storage.BlobGcDryRun, ct);
        var graceHours = Math.Max(MinGraceHours, await _settings.GetAsync<int>(SettingKeys.Storage.OrphanRetentionHours, ct));
        var startedUtc = DateTime.UtcNow;
        var untouchedSince = startedUtc - TimeSpan.FromHours(graceHours);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // 1. Raw .eml retention — drop the reference; the sweep below then
        // treats the file like any other orphan (grace period included).
        var rawEmlExpired = await ExpireRawEmlAsync(conn, dryRun, ct);

        // 2. Mark.
        var referenced = new HashSet<Hash256>();
        await using (var cmd = new NpgsqlCommand(ReferencedSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (Hash256.TryParse(reader.GetString(0), out var h)) referenced.Add(h);
            }
        }

        // 3. Sweep.
        long filesScanned = 0, bytesScanned = 0, referencedFound = 0;
        long orphanFiles = 0, orphanBytes = 0, deletedFiles = 0, deletedBytes = 0;
        var candidates = new List<BlobFileInfo>(RecheckBatchSize);

        await foreach (var file in _store.EnumerateAsync(ct))
        {
            filesScanned++;
            bytesScanned += file.SizeBytes;
            if (!Hash256.TryParse(file.ContentHash, out var h)) continue;
            if (referenced.Contains(h)) { referencedFound++; continue; }
            if (file.LastWriteUtc >= untouchedSince) continue;

            candidates.Add(file);
            if (candidates.Count >= RecheckBatchSize)
            {
                var (o, ob, d, db) = await SweepBatchAsync(conn, candidates, untouchedSince, dryRun, ct);
                orphanFiles += o; orphanBytes += ob; deletedFiles += d; deletedBytes += db;
                candidates.Clear();
            }
        }
        if (candidates.Count > 0)
        {
            var (o, ob, d, db) = await SweepBatchAsync(conn, candidates, untouchedSince, dryRun, ct);
            orphanFiles += o; orphanBytes += ob; deletedFiles += d; deletedBytes += db;
        }

        var (tmpFiles, tmpBytes) = await _store.PurgeStaleTempFilesAsync(startedUtc - StaleTempAge, delete: !dryRun, ct);

        sw.Stop();
        var snapshot = new BlobGcSnapshot(
            FinishedUtc: DateTime.UtcNow,
            Duration: sw.Elapsed,
            DryRun: dryRun,
            FilesScanned: filesScanned,
            BytesScanned: bytesScanned,
            ReferencedHashes: referenced.Count,
            ReferencedMissing: Math.Max(0, referenced.Count - referencedFound),
            OrphanFiles: orphanFiles,
            OrphanBytes: orphanBytes,
            DeletedFiles: deletedFiles,
            DeletedBytes: deletedBytes,
            StaleTempFiles: tmpFiles,
            StaleTempBytes: tmpBytes,
            RawEmlExpired: rawEmlExpired);

        _logger.LogInformation(
            "Blob GC {Mode}: scanned {Files} files ({Bytes} bytes), {Orphans} orphan(s) ({OrphanBytes} bytes), deleted {Deleted} ({DeletedBytes} bytes), {Missing} referenced blob(s) missing, {Tmp} stale temp file(s), {RawEml} raw .eml expired.",
            dryRun ? "dry run" : "run", filesScanned, bytesScanned, orphanFiles, orphanBytes,
            deletedFiles, deletedBytes, snapshot.ReferencedMissing, tmpFiles, rawEmlExpired);

        if (!dryRun && (deletedFiles > 0 || rawEmlExpired > 0 || tmpFiles > 0))
        {
            await _audit.LogAsync(new AuditEvent(
                EventType: "storage.blob_gc",
                Actor: "blob-gc",
                ActorRole: "System",
                Payload: new
                {
                    deletedFiles,
                    deletedBytes,
                    staleTempFiles = tmpFiles,
                    rawEmlExpired,
                    graceHours,
                }), ct);
        }

        return snapshot;
    }

    private async Task<(long Orphans, long OrphanBytes, long Deleted, long DeletedBytes)> SweepBatchAsync(
        NpgsqlConnection conn, List<BlobFileInfo> candidates, DateTime untouchedSince, bool dryRun, CancellationToken ct)
    {
        var hashes = candidates.Select(c => c.ContentHash).ToArray();
        var stillReferenced = (await conn.QueryAsync<string>(new CommandDefinition(
            StillReferencedSql, new { Hashes = hashes }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        long orphans = 0, orphanBytes = 0, deleted = 0, deletedBytes = 0;
        foreach (var file in candidates)
        {
            if (stillReferenced.Contains(file.ContentHash)) continue;
            orphans++;
            orphanBytes += file.SizeBytes;
            if (dryRun) continue;

            try
            {
                var freed = await _store.DeleteIfUntouchedSinceAsync(file.ContentHash, untouchedSince, ct);
                if (freed is { } b) { deleted++; deletedBytes += b; }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Blob GC could not delete {Hash}; retried next run.", file.ContentHash[..12]);
            }
        }
        return (orphans, orphanBytes, deleted, deletedBytes);
    }

    private async Task<long> ExpireRawEmlAsync(NpgsqlConnection conn, bool dryRun, CancellationToken ct)
    {
        var days = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Storage.RawEmlRetentionDays, ct), 0, 36500);
        if (days == 0) return 0; // keep forever
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);

        if (dryRun)
        {
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                RawEmlCountSql, new { Cutoff = cutoff }, cancellationToken: ct));
        }

        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                RawEmlExpireSql, new { Cutoff = cutoff, BatchSize = RawEmlBatchSize }, cancellationToken: ct));
            total += rows;
            if (rows < RawEmlBatchSize) break;
        }
        return total;
    }

    /// SHA-256 as four longs: the mark set holds every referenced hash, and
    /// 32 bytes per entry keeps a million-blob install in tens of MB.
    internal readonly record struct Hash256(ulong A, ulong B, ulong C, ulong D)
    {
        public static bool TryParse(string? hex, out Hash256 hash)
        {
            hash = default;
            if (hex is null || hex.Length != 64) return false;
            Span<ulong> parts = stackalloc ulong[4];
            for (var p = 0; p < 4; p++)
            {
                ulong v = 0;
                for (var i = p * 16; i < p * 16 + 16; i++)
                {
                    var n = Nibble(hex[i]);
                    if (n < 0) return false;
                    v = (v << 4) | (uint)n;
                }
                parts[p] = v;
            }
            hash = new Hash256(parts[0], parts[1], parts[2], parts[3]);
            return true;
        }

        // Lowercase only — the store never writes uppercase names.
        private static int Nibble(char c) =>
            c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
    }
}
