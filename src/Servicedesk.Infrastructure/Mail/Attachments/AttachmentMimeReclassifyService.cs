using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Mail.Attachments;

/// One-shot re-sniff of attachment rows the v0.1.3 inbound sniffer
/// misfiled (v0.1.7). PDFs whose bytes start with a CRLF (some mailers
/// prepend one) failed the strict <c>%PDF</c> magic check and fell through to
/// the text-vs-binary heuristic: <c>text/plain</c> when the first 512 bytes
/// were ASCII object headers, <c>application/octet-stream</c> otherwise.
/// The sniffer now tolerates leading whitespace; this service walks every
/// Ready row that carries one of those two fallback types and a
/// <c>.pdf</c> filename, re-sniffs the stored blob and rewrites the MIME
/// type where the verdict changed.
///
/// Gated by a <c>data_migrations</c> marker so it runs once per install;
/// keyset-paginated so a row whose verdict does not change (a genuinely
/// non-PDF file named .pdf) is never re-selected. Per-blob results are
/// memoised because the content-addressed store shares one blob between
/// rows (quoted-reply threads carry the same attachment across events).
/// Errors are logged and retried on a backoff; the marker is only written
/// after a clean full pass, so a crash mid-way simply resumes on restart.
public sealed class AttachmentMimeReclassifyService : BackgroundService
{
    internal const string MigrationName = "v0_1_7_resniff_whitespace_prefixed_pdfs";
    private const int BatchSize = 200;
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BetweenBatches = TimeSpan.FromMilliseconds(100);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IBlobStore _blobs;
    private readonly ILogger<AttachmentMimeReclassifyService> _logger;

    public AttachmentMimeReclassifyService(
        NpgsqlDataSource dataSource,
        IBlobStore blobs,
        ILogger<AttachmentMimeReclassifyService> logger)
    {
        _dataSource = dataSource;
        _blobs = blobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same belt-and-braces delay as the other one-shot backfills: let
        // DatabaseBootstrapper's ClearPool settle before we open connections.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var after = Guid.Empty;
        var verdictByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var rewritten = 0;
        var scanned = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await MarkerExistsAsync(stoppingToken))
                {
                    return;
                }

                var rows = await SelectBatchAsync(after, stoppingToken);
                if (rows.Count == 0)
                {
                    await WriteMarkerAsync(stoppingToken);
                    _logger.LogInformation(
                        "AttachmentMimeReclassifyService: done — scanned {Scanned} candidate rows, rewrote {Rewritten} MIME types",
                        scanned, rewritten);
                    return;
                }

                foreach (var row in rows)
                {
                    after = row.Id;
                    scanned++;

                    if (!verdictByHash.TryGetValue(row.ContentHash, out var verdict))
                    {
                        verdict = await SniffAsync(row, stoppingToken) ?? row.MimeType;
                        verdictByHash[row.ContentHash] = verdict;
                    }

                    if (string.Equals(verdict, row.MimeType, StringComparison.OrdinalIgnoreCase)) continue;

                    await UpdateMimeAsync(row.Id, verdict, stoppingToken);
                    rewritten++;
                }

                try { await Task.Delay(BetweenBatches, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AttachmentMimeReclassifyService batch failed; retrying in {Seconds}s", ErrorBackoff.TotalSeconds);
                try { await Task.Delay(ErrorBackoff, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<bool> MarkerExistsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM data_migrations WHERE name = @name)",
            new { name = MigrationName }, cancellationToken: ct));
    }

    private async Task WriteMarkerAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO data_migrations (name) VALUES (@name) ON CONFLICT (name) DO NOTHING",
            new { name = MigrationName }, cancellationToken: ct));
    }

    private async Task<List<CandidateRow>> SelectBatchAsync(Guid after, CancellationToken ct)
    {
        // Only the two fallback verdicts the old sniffer could hand a PDF, and
        // only rows whose filename says PDF — bounds the blob reads on large
        // installs to exactly the population that could have been misfiled.
        const string sql = """
            SELECT id                AS Id,
                   content_hash      AS ContentHash,
                   mime_type         AS MimeType,
                   original_filename AS OriginalFilename
              FROM attachments
             WHERE processing_state = 'Ready'
               AND content_hash IS NOT NULL
               AND mime_type IN ('text/plain', 'application/octet-stream')
               AND original_filename ILIKE '%.pdf'
               AND id > @after
             ORDER BY id
             LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CandidateRow>(new CommandDefinition(
            sql, new { after, limit = BatchSize }, cancellationToken: ct));
        return rows.ToList();
    }

    private async Task<string?> SniffAsync(CandidateRow row, CancellationToken ct)
    {
        await using var stream = await _blobs.OpenReadAsync(row.ContentHash, ct);
        if (stream is null)
        {
            _logger.LogWarning(
                "AttachmentMimeReclassifyService: blob {Hash} for attachment {Id} is missing; leaving MIME as-is",
                row.ContentHash, row.Id);
            return null;
        }

        var buffer = new byte[MimeSniffer.SniffWindowBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        // The declared type is unknown at this point (the row holds the old
        // verdict, not the sender's label); pass the current verdict so a
        // non-PDF stays exactly where it is.
        return MimeSniffer.Sniff(buffer.AsSpan(0, read), row.MimeType, row.OriginalFilename);
    }

    private async Task UpdateMimeAsync(Guid id, string mimeType, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE attachments SET mime_type = @mimeType WHERE id = @id",
            new { id, mimeType }, cancellationToken: ct));
    }

    private sealed class CandidateRow
    {
        public Guid Id { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public string OriginalFilename { get; set; } = string.Empty;
    }
}
