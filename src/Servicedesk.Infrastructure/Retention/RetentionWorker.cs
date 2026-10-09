using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Retention;

/// v0.0.101 — one generic sweep for the housekeeping tables that grew
/// without bound: expired/revoked sessions, old notifications, finished
/// attachment jobs (+ their attempt history via FK cascade), acknowledged
/// incidents and disk samples. Modelled on ActivityRetentionWorker: batched
/// deletes ordered by primary key so a first-time backlog never holds a long
/// lock, per-table retention in days (0 = keep forever), one interval.
///
/// Deliberately NOT covered: audit_log (hash-chained, tamper-evident, kept
/// unbounded by design — archival is a separate decision), survey
/// invitations (they carry the survey results), M365 report sends (send
/// log, low volume), Zammad import records (one-off import artefact).
public sealed class RetentionWorker : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IRetentionHealth _health;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        IRetentionHealth health,
        ILogger<RetentionWorker> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _health = health;
        _logger = logger;
    }

    /// One prunable table: label for logs/health, the batched DELETE (must
    /// select by primary key with LIMIT @BatchSize and filter on @Cutoff),
    /// and the setting that holds its retention in days.
    private sealed record Rule(string Table, string SettingKey, string Sql);

    private static readonly Rule[] Rules =
    {
        // Sessions that can never authenticate again: revoked, or past their
        // hard expiry. Cutoff applies to the moment they became dead so an
        // admin can still see "recently expired" sessions on the user page.
        new("user_sessions", SettingKeys.Retention.UserSessionsDays, """
            DELETE FROM user_sessions
            WHERE id IN (
                SELECT id FROM user_sessions
                WHERE (revoked_utc IS NOT NULL OR expires_utc < now())
                  AND COALESCE(revoked_utc, expires_utc) < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Notifications the user has already seen or acknowledged.
        new("user_notifications (read)", SettingKeys.Retention.NotificationsReadDays, """
            DELETE FROM user_notifications
            WHERE id IN (
                SELECT id FROM user_notifications
                WHERE (viewed_utc IS NOT NULL OR acked_utc IS NOT NULL)
                  AND created_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Unread notifications get a (much longer) separate window — a badge
        // nobody clicked for a year is noise, not a to-do.
        new("user_notifications (unread)", SettingKeys.Retention.NotificationsUnreadDays, """
            DELETE FROM user_notifications
            WHERE id IN (
                SELECT id FROM user_notifications
                WHERE viewed_utc IS NULL AND acked_utc IS NULL
                  AND created_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Finished attachment jobs. DeadLettered rows are excluded on purpose:
        // they sit on the Health page until an admin requeues or cancels
        // them, and silently vanishing would clear a Critical. attempts go
        // with the job (ON DELETE CASCADE).
        new("attachment_jobs", SettingKeys.Retention.AttachmentJobsDays, """
            DELETE FROM attachment_jobs
            WHERE id IN (
                SELECT id FROM attachment_jobs
                WHERE state IN ('Succeeded','Failed','Cancelled')
                  AND updated_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // Acknowledged incidents (the Health archive). Open ones are never touched.
        new("incidents", SettingKeys.Retention.IncidentsDays, """
            DELETE FROM incidents
            WHERE id IN (
                SELECT id FROM incidents
                WHERE acknowledged_utc IS NOT NULL AND acknowledged_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // v0.1.14 — Insights "opened without action" source. Closed sessions
        // age from their close moment; one still open (ticket left in the
        // sidebar for ages) from when it was opened.
        new("ticket_open_sessions", SettingKeys.Retention.TicketOpenSessionsDays, """
            DELETE FROM ticket_open_sessions
            WHERE id IN (
                SELECT id FROM ticket_open_sessions
                WHERE COALESCE(closed_utc, opened_utc) < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // v0.1.31 — Insights Rewind snapshots, aged by the moment a snapshot
        // stopped being current (an unchanged weekend is one row).
        new("rewind_snapshots", SettingKeys.Retention.RewindSnapshotsDays, """
            DELETE FROM rewind_snapshots
            WHERE id IN (
                SELECT id FROM rewind_snapshots
                WHERE last_seen_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        // v0.1.32 — Insights Workflow pickup records.
        new("workflow_pickups", SettingKeys.Retention.WorkflowPickupsDays, """
            DELETE FROM workflow_pickups
            WHERE id IN (
                SELECT id FROM workflow_pickups
                WHERE picked_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
        new("blob_disk_samples", SettingKeys.Retention.BlobDiskSamplesDays, """
            DELETE FROM blob_disk_samples
            WHERE id IN (
                SELECT id FROM blob_disk_samples
                WHERE sampled_utc < @Cutoff
                ORDER BY id
                LIMIT @BatchSize
            )
            """),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let bootstrap + the other workers settle first.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = 6;
            using (var run = PerfWorkerRun.Start("retention"))
            {
                try
                {
                    intervalHours = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Retention.RunIntervalHours, stoppingToken), 1, 168);
                    await SweepAsync(TimeSpan.FromHours(intervalHours), run, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "RetentionWorker sweep failed.");
                    _health.RecordFailure(ex.Message);
                }
            }

            try { await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(TimeSpan interval, PerfWorkerRun run, CancellationToken ct)
    {
        var batchSize = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Retention.BatchSize, ct), 100, 50_000);
        var sw = Stopwatch.StartNew();
        var deleted = new Dictionary<string, long>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var rule in Rules)
        {
            ct.ThrowIfCancellationRequested();
            var days = Math.Clamp(await _settings.GetAsync<int>(rule.SettingKey, ct), 0, 3650);
            if (days == 0) { deleted[rule.Table] = 0; continue; } // 0 = keep forever

            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(days);
            long total = 0;
            while (!ct.IsCancellationRequested)
            {
                var rows = await conn.ExecuteAsync(new CommandDefinition(
                    rule.Sql, new { Cutoff = cutoff, BatchSize = batchSize }, cancellationToken: ct));
                total += rows;
                if (rows < batchSize) break;
            }
            deleted[rule.Table] = total;
            if (total > 0)
                _logger.LogInformation("RetentionWorker pruned {Count} rows from {Table} older than {Days}d.", total, rule.Table, days);
        }

        ct.ThrowIfCancellationRequested();
        var stagedDays = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Retention.StagedAttachmentsDays, ct), 0, 3650);
        var staged = stagedDays == 0 ? 0 : await SweepStagedAttachmentsAsync(conn, stagedDays, ct);
        deleted[StagedAttachmentsLabel] = staged;
        if (staged > 0)
            _logger.LogInformation("RetentionWorker pruned {Count} unposted composer uploads older than {Days}d.", staged, stagedDays);

        sw.Stop();
        run.AddItems(deleted.Values.Sum());
        _health.RecordRun(deleted, sw.Elapsed, DateTime.UtcNow + interval);
    }

    private const string StagedAttachmentsLabel = "attachments (unposted uploads)";

    // Candidates per round. Each round runs one reference check over the
    // bodies written since the oldest candidate, so it stays bounded.
    private const int StagedCandidatesPerRound = 500;

    /// v0.1.33 — uploads pasted into a composer that were never posted stay
    /// staged (owner_kind='Ticket', event_id NULL) forever: invisible in the
    /// timeline, still downloadable with queue access. A row is removed only
    /// when it is older than the cutoff AND nothing points at it:
    ///   • no saved workspace draft of any user mentions its id;
    ///   • no note / mail / ticket body, event revision, KB article or compose
    ///     template mentions it — a body copied into another composer (or
    ///     ticket) keeps rendering through the original staged row. Only
    ///     events written or edited after the upload can reference it, which
    ///     bounds the largest scan.
    /// The blob itself stays: the blob store is content-addressed and has its
    /// own GC. Deletes re-check the staged guard so a post racing the sweep
    /// keeps its file.
    private static async Task<long> SweepStagedAttachmentsAsync(NpgsqlConnection conn, int days, CancellationToken ct)
    {
        const string candidatesSql = """
            SELECT id AS Id, created_utc AS CreatedUtc
              FROM attachments
             WHERE owner_kind = 'Ticket'
               AND event_id IS NULL
               AND created_utc < @Cutoff
               AND (@AfterUtc::timestamptz IS NULL OR (created_utc, id) > (@AfterUtc, @AfterId))
             ORDER BY created_utc, id
             LIMIT @Limit
            """;
        // Bodies are matched by pulling every attachment id out of them in
        // one regex pass and hash-joining the candidates — not strpos per
        // candidate per row, which multiplied the scan by the batch size.
        const string referencedSql = """
            WITH c(id) AS (SELECT unnest(@Ids::text[])),
            body_refs(id) AS (
                SELECT lower(m[1])
                  FROM ticket_events e
                 CROSS JOIN LATERAL regexp_matches(e.body_html, '/attachments/([0-9a-fA-F-]{36})', 'g') m
                 WHERE (e.created_utc >= @MinCreated OR e.edited_utc >= @MinCreated)
                   AND e.body_html LIKE '%/attachments/%'
                UNION
                SELECT lower(m[1])
                  FROM ticket_event_revisions r
                 CROSS JOIN LATERAL regexp_matches(r.body_html_before, '/attachments/([0-9a-fA-F-]{36})', 'g') m
                 WHERE r.edited_utc >= @MinCreated
                   AND r.body_html_before LIKE '%/attachments/%'
                UNION
                SELECT lower(m[1])
                  FROM ticket_bodies b
                 CROSS JOIN LATERAL regexp_matches(b.body_html, '/attachments/([0-9a-fA-F-]{36})', 'g') m
                 WHERE b.body_html LIKE '%/attachments/%'
                UNION
                -- A screenshot copied from a ticket composer into a KB
                -- article or a compose template still points at the ticket row.
                SELECT lower(m[1])
                  FROM kb_article_translations k
                 CROSS JOIN LATERAL regexp_matches(k.body_html, '/attachments/([0-9a-fA-F-]{36})', 'g') m
                 WHERE k.body_html LIKE '%/attachments/%'
                UNION
                SELECT lower(m[1])
                  FROM compose_templates t
                 CROSS JOIN LATERAL regexp_matches(t.body_html, '/attachments/([0-9a-fA-F-]{36})', 'g') m
                 WHERE t.body_html LIKE '%/attachments/%'
            )
            SELECT c.id FROM c JOIN body_refs r ON r.id = c.id
            UNION
            SELECT c.id FROM c
             WHERE EXISTS (SELECT 1 FROM user_preferences p
                            WHERE p.pref_key LIKE 'workspace:%'
                              AND strpos(lower(p.pref_value), c.id) > 0)
            """;
        const string deleteSql = """
            DELETE FROM attachments
             WHERE id = ANY(@Ids)
               AND owner_kind = 'Ticket'
               AND event_id IS NULL
               AND created_utc < @Cutoff
            """;

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);
        DateTime? afterUtc = null;
        var afterId = Guid.Empty;
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = (await conn.QueryAsync<StagedCandidate>(new CommandDefinition(candidatesSql, new
            {
                Cutoff = cutoff,
                AfterUtc = afterUtc,
                AfterId = afterId,
                Limit = StagedCandidatesPerRound,
            }, cancellationToken: ct))).ToList();
            if (candidates.Count == 0) break;

            var ids = candidates.Select(c => c.Id.ToString()).ToArray();
            var referenced = (await conn.QueryAsync<string>(new CommandDefinition(referencedSql, new
            {
                Ids = ids,
                MinCreated = candidates[0].CreatedUtc,
            }, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var orphans = candidates.Where(c => !referenced.Contains(c.Id.ToString())).Select(c => c.Id).ToArray();
            if (orphans.Length > 0)
            {
                total += await conn.ExecuteAsync(new CommandDefinition(deleteSql, new
                {
                    Ids = orphans,
                    Cutoff = cutoff,
                }, cancellationToken: ct));
            }

            if (candidates.Count < StagedCandidatesPerRound) break;
            afterUtc = candidates[^1].CreatedUtc;
            afterId = candidates[^1].Id;
        }
        return total;
    }

    private sealed class StagedCandidate
    {
        public Guid Id { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
