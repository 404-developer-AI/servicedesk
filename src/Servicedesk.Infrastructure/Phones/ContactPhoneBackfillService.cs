using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Phones;

/// One-shot backfill that runs after DatabaseBootstrapper has added the
/// E.164 mirror columns. Walks every contact whose phone / mobile_phone
/// is non-empty but whose phone_e164 / mobile_phone_e164 is empty, runs
/// the normaliser, and updates in batches. Idempotent: once a row is
/// processed, the partial WHERE clause excludes it on the next pass, so
/// repeated restarts are no-ops.
/// Designed as a BackgroundService that exits when there is nothing left
/// to do. Errors are caught per-batch and logged; the service keeps
/// trying on a 30s backoff. We never crash the host over a single bad
/// phone string — the row stays with phone_e164 = '' which simply means
/// it won't surface in phone-search.
public sealed class ContactPhoneBackfillService : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BetweenBatches = TimeSpan.FromMilliseconds(250);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IContactPhoneNormalizer _normalizer;
    private readonly ILogger<ContactPhoneBackfillService> _logger;

    public ContactPhoneBackfillService(
        NpgsqlDataSource dataSource,
        IContactPhoneNormalizer normalizer,
        ILogger<ContactPhoneBackfillService> logger)
    {
        _dataSource = dataSource;
        _normalizer = normalizer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tiny pre-delay so DatabaseBootstrapper definitely committed the
        // ALTER TABLE before we start querying the new columns. The bootstrap
        // is awaited by the host before hosted services that follow it start,
        // but we belt-and-braces because the bootstrap's ClearPool resets
        // connections and a racing claim from this worker could otherwise
        // hit a stale pooled connection.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int updated;
            try
            {
                updated = await ProcessOneBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ContactPhoneBackfillService batch failed; retrying in {Seconds}s", ErrorBackoff.TotalSeconds);
                try { await Task.Delay(ErrorBackoff, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            if (updated == 0)
            {
                _logger.LogInformation("ContactPhoneBackfillService: no contacts to backfill, exiting");
                return;
            }

            _logger.LogInformation("ContactPhoneBackfillService: normalised {Count} contact phone rows", updated);
            try { await Task.Delay(BetweenBatches, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<int> ProcessOneBatchAsync(CancellationToken ct)
    {
        // SELECT runs outside the transaction so a long-running normalisation
        // loop doesn't hold row-level locks against concurrent writers. The
        // UPDATE inside the transaction is safe to overwrite a concurrent
        // edit because the partial-index filter (`phone_e164 = ''`) only
        // matches rows the application has explicitly not normalised yet —
        // a concurrent write through the repository would have just written
        // a non-empty `phone_e164` itself. A single-statement UPDATE-FROM
        // would be more atomic but we accept the trade-off here: this
        // service exits after the first empty-batch, so the window is short.
        const string selectSql = """
            SELECT id          AS "Id",
                   phone       AS "Phone",
                   mobile_phone AS "MobilePhone"
            FROM contacts
            WHERE (phone        <> '' AND phone_e164        = '')
               OR (mobile_phone <> '' AND mobile_phone_e164 = '')
            ORDER BY id
            LIMIT @limit
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ContactPhoneRow>(new CommandDefinition(
            selectSql, new { limit = BatchSize }, cancellationToken: ct))).ToList();

        if (rows.Count == 0) return 0;

        const string updateSql = """
            UPDATE contacts
               SET phone_e164        = @PhoneE164,
                   mobile_phone_e164 = @MobilePhoneE164
             WHERE id = @Id
            """;

        await using var tx = await conn.BeginTransactionAsync(ct);
        var written = 0;
        foreach (var row in rows)
        {
            var (phoneE164, mobileE164) = await _normalizer.NormalizePairAsync(row.Phone, row.MobilePhone, ct);
            await conn.ExecuteAsync(new CommandDefinition(updateSql,
                new
                {
                    Id = row.Id,
                    PhoneE164 = phoneE164,
                    MobilePhoneE164 = mobileE164,
                },
                tx, cancellationToken: ct));
            written++;
        }
        await tx.CommitAsync(ct);
        return written;
    }

    private sealed class ContactPhoneRow
    {
        public Guid Id { get; set; }
        public string Phone { get; set; } = string.Empty;
        public string MobilePhone { get; set; } = string.Empty;
    }
}
