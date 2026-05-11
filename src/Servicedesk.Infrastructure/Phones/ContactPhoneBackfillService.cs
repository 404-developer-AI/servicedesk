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

        // Track which contact-ids we've already touched in this run so
        // unparseable phone strings can't trap us in an infinite re-process
        // loop. The WHERE clause matches `phone <> '' AND phone_e164 = ''`
        // — but for a phone like "n/a" or "tbd" the normalizer returns ''
        // and the row stays a forever-candidate. Once the same id comes
        // back in a subsequent batch, we know we're cycling and exit.
        var seen = new HashSet<Guid>();
        var unparseable = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            BatchOutcome outcome;
            try
            {
                outcome = await ProcessOneBatchAsync(seen, stoppingToken);
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

            unparseable += outcome.Unparseable;

            if (outcome.Selected == 0)
            {
                _logger.LogInformation("ContactPhoneBackfillService: no contacts to backfill, exiting");
                return;
            }

            // Every selected row was already processed in this run — that
            // means all remaining candidates are unparseable and looping
            // again would just churn the same rows. Log the tombstone
            // count once and exit so the host stops paying for it.
            if (outcome.Fresh == 0)
            {
                _logger.LogInformation(
                    "ContactPhoneBackfillService: {Unparseable} unparseable phone rows remain (no further progress possible); exiting",
                    unparseable);
                return;
            }

            _logger.LogInformation(
                "ContactPhoneBackfillService: normalised {Count} contact phone rows ({Unparseable} unparseable so far)",
                outcome.Normalised, unparseable);
            try { await Task.Delay(BetweenBatches, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private readonly record struct BatchOutcome(int Selected, int Fresh, int Normalised, int Unparseable);

    private async Task<BatchOutcome> ProcessOneBatchAsync(HashSet<Guid> seen, CancellationToken ct)
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

        if (rows.Count == 0) return new BatchOutcome(0, 0, 0, 0);

        const string updateSql = """
            UPDATE contacts
               SET phone_e164        = @PhoneE164,
                   mobile_phone_e164 = @MobilePhoneE164
             WHERE id = @Id
            """;

        await using var tx = await conn.BeginTransactionAsync(ct);
        var fresh = 0;
        var normalised = 0;
        var unparseable = 0;
        foreach (var row in rows)
        {
            // Skip rows we've already processed in this run — the row is
            // still being selected because the normaliser couldn't parse
            // its phone. Re-running would just churn an empty UPDATE and
            // the WHERE clause keeps matching it forever otherwise.
            if (!seen.Add(row.Id)) continue;
            fresh++;

            var (phoneE164, mobileE164) = await _normalizer.NormalizePairAsync(row.Phone, row.MobilePhone, ct);
            // If both new values are empty, the contact's phone columns
            // are non-empty but unparseable. Skip the write — it would
            // just rewrite the empty values and leave the row matching
            // the SELECT clause on the next pass.
            var phoneStillEmpty = row.Phone.Length > 0 && phoneE164.Length == 0;
            var mobileStillEmpty = row.MobilePhone.Length > 0 && mobileE164.Length == 0;
            if (phoneStillEmpty && (row.MobilePhone.Length == 0 || mobileStillEmpty))
            {
                unparseable++;
                continue;
            }

            await conn.ExecuteAsync(new CommandDefinition(updateSql,
                new
                {
                    Id = row.Id,
                    PhoneE164 = phoneE164,
                    MobilePhoneE164 = mobileE164,
                },
                tx, cancellationToken: ct));
            normalised++;
        }
        await tx.CommitAsync(ct);
        return new BatchOutcome(Selected: rows.Count, Fresh: fresh, Normalised: normalised, Unparseable: unparseable);
    }

    private sealed class ContactPhoneRow
    {
        public Guid Id { get; set; }
        public string Phone { get; set; } = string.Empty;
        public string MobilePhone { get; set; } = string.Empty;
    }
}
