using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Background worker that processes Zammad dry-run jobs queued by
/// <see cref="ZammadDryRunService"/>. Reads run-ids from the in-memory
/// <see cref="IZammadDryRunQueue"/> channel; for each run:
/// <list type="number">
/// <item>Marks the run row as <c>running</c>.</item>
/// <item>Loads the persisted source-filter + the per-table mapping
/// dictionary.</item>
/// <item>Determines the ticket-id set — either the explicit list from
/// the picker selection, or a re-query of <c>/tickets/search</c> when
/// the admin chose "Select all matching".</item>
/// <item>For each ticket: fetches the full ticket through
/// <see cref="IZammadApiClient.GetTicketAsync"/>, resolves the mapping,
/// inserts a <c>zammad_import_records</c> row and bumps the run's
/// JSONB totals counter.</item>
/// <item>Marks the run as <c>completed</c> / <c>failed</c>, or exits
/// early when the admin cancelled it mid-run.</item>
/// </list>
///
/// Singleton hosted service — at most one worker per process. Internal
/// per-run work runs sequentially through the ticket list so the
/// Postgres + Zammad load profile stays predictable. A future scale
/// tweak could spawn a <see cref="Parallel.ForEachAsync"/> with a small
/// <see cref="SemaphoreSlim"/> but the bottleneck up to ~1K tickets is
/// almost always Zammad-side latency, not our side.
public sealed class ZammadDryRunWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IZammadDryRunQueue _queue;
    private readonly ILogger<ZammadDryRunWorker> _logger;

    /// How often to flush the totals JSONB back to the run row. Flushing
    /// after every ticket on a 1K-ticket run is 1K writes; every 10
    /// tickets cuts that to 100 + a final-flush, still keeps the UI's
    /// 2s progress polling smooth.
    private const int TotalsFlushBatchSize = 10;

    /// Fallback for <see cref="SettingKeys.Zammad.SelectAllMatchingHardCap"/>
    /// when the setting is missing or out-of-range. Kept in sync with
    /// the default in <see cref="SettingKeys"/>; the per-run resolver
    /// reads the setting and clamps to [100, 200_000].
    private const int SelectAllMatchingHardCapDefault = 20_000;
    private const int SelectAllMatchingHardCapMin = 100;
    private const int SelectAllMatchingHardCapMax = 200_000;

    public ZammadDryRunWorker(
        IServiceProvider sp,
        IZammadDryRunQueue queue,
        ILogger<ZammadDryRunWorker> logger)
    {
        _sp = sp;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same staggered-start pattern as the other integration workers:
        // give DatabaseBootstrapper, secret store warmup and IIntegrationAuditLogger
        // a head start before the first run could land. 30 s is plenty
        // on Windows dev + Docker prod.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await foreach (var runId in _queue.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) return;
                await ProcessRunAsync(runId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown — quiet exit.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ZammadDryRunWorker main loop terminated unexpectedly; worker is now offline until restart.");
            throw;
        }
    }

    private async Task ProcessRunAsync(Guid runId, CancellationToken stoppingToken)
    {
        // Per-run service scope so transient dependencies (Dapper conns,
        // audit logger) are disposed cleanly when the run finishes.
        using var scope = _sp.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var zammad = scope.ServiceProvider.GetRequiredService<IZammadApiClient>();
        var mappings = scope.ServiceProvider.GetRequiredService<IZammadMappingService>();
        var resolver = scope.ServiceProvider.GetRequiredService<IZammadTicketResolver>();
        var writer = scope.ServiceProvider.GetRequiredService<IZammadImportWriter>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        var integrationAudit = scope.ServiceProvider.GetRequiredService<IIntegrationAuditLogger>();

        try
        {
            // Sanity-check: master switch off → mark run as failed
            // immediately so the UI doesn't show a hanging "running".
            var enabled = await settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, stoppingToken);
            if (!enabled)
            {
                await MarkRunFailedAsync(dataSource, runId,
                    "Zammad integration is disabled. Toggle it on first.", stoppingToken);
                return;
            }

            var loaded = await LoadRunAsync(dataSource, runId, stoppingToken);
            if (loaded is null)
            {
                _logger.LogWarning(
                    "ZammadDryRunWorker: run {RunId} not found in DB; skipping.", runId);
                return;
            }
            var (kind, status, filter, totals) = loaded.Value;

            if (status is "cancelled" or "completed" or "failed")
            {
                // Already terminal; nothing to do. Can happen when the
                // admin cancels before the worker picks up the message.
                return;
            }

            await MarkRunStatusAsync(dataSource, runId, "running", stoppingToken);

            if (string.Equals(kind, "import", StringComparison.Ordinal))
            {
                // Resolve attachment-size cap once per run. Matches the
                // Graph inbound + ticket-upload paths so Zammad imports
                // inherit the same admin-tunable limit. Oversized
                // attachments are skipped (the ticket still imports; the
                // per-record reasons list captures which files were
                // dropped) rather than failing the whole ticket.
                long maxAttachmentBytes;
                try
                {
                    maxAttachmentBytes = await settings.GetAsync<long>(
                        SettingKeys.Storage.MaxAttachmentBytes, stoppingToken);
                    if (maxAttachmentBytes <= 0) maxAttachmentBytes = 26_214_400;
                }
                catch
                {
                    maxAttachmentBytes = 26_214_400;
                }
                var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

                await ProcessImportRunAsync(
                    dataSource, zammad, writer, audit, blobs, maxAttachmentBytes,
                    runId, filter, totals, stoppingToken);
                return;
            }

            await LogLifecycleAsync(audit, ZammadEventTypes.DryRunStarted, runId, new
            {
                runId,
                plannedTotal = totals.PlannedTotal,
                ticketIdCount = filter?.TicketIds?.Count,
                selectAllMatching = filter?.SelectAllMatching == true,
            }, stoppingToken);

            // Load mappings once. The hot loop only does dictionary
            // lookups + a contact-by-email DB hit per ticket.
            var dict = await mappings.LoadDictionaryAsync(stoppingToken);

            // Resolve the hard cap from settings per-run so an admin can
            // raise it for a one-off bulk migration without touching code.
            // Out-of-range → fall back to the default; clamp at the edges.
            var rawCap = await settings.GetAsync<int>(SettingKeys.Zammad.SelectAllMatchingHardCap, stoppingToken);
            var hardCap = rawCap <= 0
                ? SelectAllMatchingHardCapDefault
                : Math.Clamp(rawCap, SelectAllMatchingHardCapMin, SelectAllMatchingHardCapMax);

            // Resolve ticket-id list: explicit selection, or re-search.
            IReadOnlyList<long> ticketIds;
            try
            {
                ticketIds = await ResolveTicketIdsAsync(filter, zammad, hardCap, stoppingToken);
            }
            catch (ZammadApiException ex)
            {
                await MarkRunFailedAsync(dataSource, runId,
                    "Could not resolve ticket selection: " + ex.Message, stoppingToken);
                return;
            }

            if (ticketIds.Count == 0)
            {
                await CompleteRunAsync(dataSource, runId, totals with { PlannedTotal = 0 }, stoppingToken);
                await LogLifecycleAsync(audit, ZammadEventTypes.DryRunFinished, runId, new
                {
                    runId,
                    status = "completed",
                    total = 0,
                }, stoppingToken);
                return;
            }

            // If the picker didn't pass a planned total (Select-all-
            // matching path), seed it now that we know how many tickets
            // actually came back.
            if (totals.PlannedTotal is null)
            {
                totals = totals with { PlannedTotal = ticketIds.Count };
                await PersistTotalsAsync(dataSource, runId, totals, stoppingToken);
            }

            var pending = 0;
            foreach (var ticketId in ticketIds)
            {
                if (stoppingToken.IsCancellationRequested) return;

                // Per-ticket cancellation probe — admin clicked Cancel
                // on the run detail page since we started. Reading the
                // status column is one tiny lookup and saves the rest
                // of the run when an admin realises the filter was wrong.
                var live = await GetRunStatusAsync(dataSource, runId, stoppingToken);
                if (live == "cancelled")
                {
                    await PersistTotalsAsync(dataSource, runId, totals, stoppingToken);
                    await LogLifecycleAsync(audit, ZammadEventTypes.DryRunCancelled, runId, new
                    {
                        runId,
                        processedSoFar = totals.Processed,
                    }, stoppingToken);
                    return;
                }

                totals = await ProcessTicketAsync(
                    dataSource, resolver, dict, runId, ticketId, totals, stoppingToken);
                pending++;
                if (pending >= TotalsFlushBatchSize)
                {
                    await PersistTotalsAsync(dataSource, runId, totals, stoppingToken);
                    pending = 0;
                }
            }

            await CompleteRunAsync(dataSource, runId, totals, stoppingToken);
            await LogLifecycleAsync(audit, ZammadEventTypes.DryRunFinished, runId, new
            {
                runId,
                status = "completed",
                totals,
            }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown — don't mark failed (we'll resume on next
            // boot if the row is still pending).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ZammadDryRunWorker crashed processing run {RunId}", runId);
            await MarkRunFailedAsync(dataSource, runId,
                "Worker crashed: " + ex.Message, CancellationToken.None);
            await LogLifecycleAsync(audit, ZammadEventTypes.DryRunFinished, runId, new
            {
                runId,
                status = "failed",
                error = ex.Message,
            }, CancellationToken.None);
        }
    }

    // ---- real-import path ---------------------------------------------

    /// Walks the prior dry-run's mapped records, fetches articles for
    /// each upstream ticket, and hands the snapshot + articles to
    /// <see cref="IZammadImportWriter"/>. Per-ticket records land in
    /// the import-run with result='imported' / 'already_imported' /
    /// 'failed'.
    private async Task ProcessImportRunAsync(
        NpgsqlDataSource ds,
        IZammadApiClient zammad,
        IZammadImportWriter writer,
        IAuditLogger audit,
        IBlobStore blobs,
        long maxAttachmentBytes,
        Guid runId,
        ZammadImportSourceFilter? filter,
        ZammadImportTotals totals,
        CancellationToken ct)
    {
        var dryRunId = filter?.DryRunId;
        if (dryRunId is null)
        {
            await MarkRunFailedAsync(ds, runId,
                "Import run is missing the parent dry-run id in its source filter.", ct);
            return;
        }

        await LogLifecycleAsync(audit, ZammadEventTypes.ImportStarted, runId, new
        {
            runId,
            dryRunId = dryRunId.Value,
            plannedTotal = totals.PlannedTotal,
        }, ct);

        var inputs = await LoadMappedDryRunRecordsAsync(ds, dryRunId.Value, ct);
        if (inputs.Count == 0)
        {
            await CompleteRunAsync(ds, runId, totals with { PlannedTotal = 0 }, ct);
            await LogLifecycleAsync(audit, ZammadEventTypes.ImportFinished, runId, new
            {
                runId,
                status = "completed",
                total = 0,
            }, ct);
            return;
        }
        if (totals.PlannedTotal is null || totals.PlannedTotal == 0)
        {
            totals = totals with { PlannedTotal = inputs.Count };
            await PersistTotalsAsync(ds, runId, totals, ct);
        }

        // Resolved upstream authors are cached for the whole run: the same
        // agent typically posts across many tickets, so one /users/{id}
        // fetch + local-email match per distinct Zammad user is plenty.
        var authorCache = new Dictionary<long, ZammadAuthorAttribution>();

        var pending = 0;
        foreach (var snapshot in inputs)
        {
            if (ct.IsCancellationRequested) return;
            var live = await GetRunStatusAsync(ds, runId, ct);
            if (live == "cancelled")
            {
                await PersistTotalsAsync(ds, runId, totals, ct);
                await LogLifecycleAsync(audit, ZammadEventTypes.ImportCancelled, runId, new
                {
                    runId,
                    processedSoFar = totals.Processed,
                }, ct);
                return;
            }

            ZammadImportWriteResult writeResult;
            IReadOnlyList<string> reasons = Array.Empty<string>();
            try
            {
                var articles = await zammad.ListArticlesAsync(snapshot.ZammadTicketId, ct);
                var (plans, attachmentReasons) = await StageAttachmentsAsync(
                    zammad, blobs, snapshot.ZammadTicketId, articles,
                    maxAttachmentBytes, ct);
                var authors = await ResolveArticleAuthorsAsync(ds, zammad, articles, authorCache, ct);

                var input = new ZammadImportWriteInput(
                    ZammadTicketId: snapshot.ZammadTicketId,
                    ZammadTicketNumber: snapshot.ZammadTicketNumber,
                    ZammadTicketTitle: snapshot.ZammadTicketTitle ?? "(no subject)",
                    ContactId: snapshot.ContactId,
                    QueueId: snapshot.QueueId,
                    StatusId: snapshot.StatusId,
                    PriorityId: snapshot.PriorityId,
                    Articles: articles,
                    Attachments: plans,
                    PendingTillUtc: snapshot.PendingTillUtc,
                    Authors: authors);
                writeResult = await writer.WriteAsync(input, ct);

                // Attachment-level skips don't fail the ticket — surface
                // them as record-reasons alongside whatever the writer
                // returned so the admin can scan one column for gaps.
                if (attachmentReasons.Count > 0)
                {
                    reasons = attachmentReasons;
                }
            }
            catch (ZammadApiException ex)
            {
                writeResult = new ZammadImportWriteResult(
                    ZammadImportRecordResult.Failed, null,
                    "articles_fetch_failed:" + (ex.UpstreamErrorCode ?? "unknown"));
                reasons = new[] { writeResult.FailureReason ?? "articles_fetch_failed" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ZammadImportWriter threw on ticket {ZammadId} in run {RunId}",
                    snapshot.ZammadTicketId, runId);
                writeResult = new ZammadImportWriteResult(
                    ZammadImportRecordResult.Failed, null, ex.Message);
                reasons = new[] { "writer_exception" };
            }

            // Persist a record row in the IMPORT run. The mapping JSONB
            // snapshot is preserved as-is from the dry-run plus the
            // resulting local ticket id (or NULL on failure).
            await InsertImportRecordAsync(
                ds, runId, snapshot, writeResult, reasons, ct);
            totals = BumpImportTotals(totals, writeResult.Result);
            pending++;
            if (pending >= TotalsFlushBatchSize)
            {
                await PersistTotalsAsync(ds, runId, totals, ct);
                pending = 0;
            }
        }

        await CompleteRunAsync(ds, runId, totals, ct);
        // Bulk load done — refresh planner statistics so the admin's first
        // clicks through the imported tickets don't run on stale estimates.
        await PostgresStatistics.AnalyzeAsync(ds, _logger, ct,
            "tickets", "ticket_bodies", "ticket_events", "ticket_event_search", "attachments");
        await LogLifecycleAsync(audit, ZammadEventTypes.ImportFinished, runId, new
        {
            runId,
            status = "completed",
            totals,
        }, ct);
    }

    /// Resolves the upstream author for the agent/system articles in one
    /// ticket. Customer articles are skipped — they keep their contact
    /// attribution in the writer. Results are memoised in <paramref name="cache"/>
    /// so the same Zammad user is fetched + email-matched at most once per
    /// run. The returned map is keyed by Zammad user id (created_by_id) and
    /// only contains entries the writer needs for this ticket.
    private static async Task<IReadOnlyDictionary<long, ZammadAuthorAttribution>> ResolveArticleAuthorsAsync(
        NpgsqlDataSource ds,
        IZammadApiClient zammad,
        IReadOnlyList<ZammadArticle> articles,
        Dictionary<long, ZammadAuthorAttribution> cache,
        CancellationToken ct)
    {
        var map = new Dictionary<long, ZammadAuthorAttribution>();
        foreach (var a in articles)
        {
            if (string.Equals(a.Sender, "Customer", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.CreatedById is not long zammadUserId) continue;
            if (map.ContainsKey(zammadUserId)) continue;

            if (!cache.TryGetValue(zammadUserId, out var attribution))
            {
                attribution = await ResolveOneAuthorAsync(ds, zammad, zammadUserId, ct);
                cache[zammadUserId] = attribution;
            }
            map[zammadUserId] = attribution;
        }
        return map;
    }

    /// One upstream-author lookup. Mirrors the KB-import rule: a Zammad user
    /// whose email matches a local user links the real user (live identity);
    /// otherwise we keep a display name ("First Last", falling back to email
    /// then login) as a plain label. Both null when the user can't be
    /// fetched, leaving the event anonymous as before.
    private static async Task<ZammadAuthorAttribution> ResolveOneAuthorAsync(
        NpgsqlDataSource ds,
        IZammadApiClient zammad,
        long zammadUserId,
        CancellationToken ct)
    {
        ZammadUser? zUser;
        try { zUser = await zammad.GetUserAsync(zammadUserId, ct); }
        catch { zUser = null; }
        if (zUser is null) return new ZammadAuthorAttribution(null, null);

        if (!string.IsNullOrWhiteSpace(zUser.Email))
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var localId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM users WHERE LOWER(email) = LOWER(@Email)",
                new { Email = zUser.Email!.Trim() }, cancellationToken: ct));
            if (localId is not null) return new ZammadAuthorAttribution(localId, null);
        }

        var name = string.Join(" ", new[] { zUser.FirstName, zUser.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = !string.IsNullOrWhiteSpace(zUser.Email) ? zUser.Email!.Trim()
                 : !string.IsNullOrWhiteSpace(zUser.Login) ? zUser.Login!.Trim()
                 : null;
        }
        return new ZammadAuthorAttribution(null, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// Walks every attachment manifest entry on every article and streams
    /// the bytes into the blob store, building a list of
    /// <see cref="ZammadImportAttachmentPlan"/>s the writer consumes inside
    /// its tx. Behaviour notes:
    /// <list type="bullet">
    /// <item>Oversized attachments (advertised size &gt; cap) are skipped
    /// without contacting Zammad; the reason list records
    /// <c>attachment_too_large:&lt;id&gt;</c>.</item>
    /// <item>Per-attachment fetch failures (404, transport, 5xx) are
    /// logged and skipped — the ticket still imports without that
    /// attachment; reason <c>attachment_fetch_failed:&lt;id&gt;</c>.</item>
    /// <item>Empty manifests yield an empty plan list; the writer then
    /// inserts no <c>attachments</c> rows for that article.</item>
    /// </list>
    /// Reasons are deliberately compact (token-prefixed ids) so they
    /// survive the <c>zammad_import_records.unresolved_reasons</c> column's
    /// existing CHECK length without bloating the per-run JSONB.
    private async Task<(IReadOnlyList<ZammadImportAttachmentPlan> Plans, IReadOnlyList<string> Reasons)>
        StageAttachmentsAsync(
            IZammadApiClient zammad,
            IBlobStore blobs,
            long zammadTicketId,
            IReadOnlyList<ZammadArticle> articles,
            long maxAttachmentBytes,
            CancellationToken ct)
    {
        var plans = new List<ZammadImportAttachmentPlan>();
        var reasons = new List<string>();
        foreach (var article in articles)
        {
            if (article.Attachments.Count == 0) continue;

            foreach (var att in article.Attachments)
            {
                if (att.SizeBytes > maxAttachmentBytes)
                {
                    reasons.Add($"attachment_too_large:{att.Id}");
                    continue;
                }

                try
                {
                    await using var stream = await zammad.FetchAttachmentBytesAsync(
                        zammadTicketId, article.Id, att.Id, ct);
                    var written = await blobs.WriteAsync(stream, ct);
                    plans.Add(new ZammadImportAttachmentPlan(
                        ZammadArticleId: article.Id,
                        ZammadAttachmentId: att.Id,
                        Filename: att.Filename,
                        SizeBytes: written.SizeBytes,
                        MimeType: att.MimeType,
                        IsInline: att.IsInline,
                        ContentId: att.ContentId,
                        ContentHash: written.ContentHash));
                }
                catch (OperationCanceledException) { throw; }
                catch (ZammadApiException ex)
                {
                    _logger.LogWarning(
                        "Zammad attachment fetch failed for ticket {TicketId} article {ArticleId} attachment {AttId}: http={Status} upstream={Upstream}",
                        zammadTicketId, article.Id, att.Id, ex.HttpStatus, ex.UpstreamErrorCode);
                    reasons.Add($"attachment_fetch_failed:{att.Id}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Zammad attachment write failed for ticket {TicketId} article {ArticleId} attachment {AttId}",
                        zammadTicketId, article.Id, att.Id);
                    reasons.Add($"attachment_write_failed:{att.Id}");
                }
            }
        }
        return (plans, reasons);
    }

    private static ZammadImportTotals BumpImportTotals(ZammadImportTotals t, string result)
    {
        return result switch
        {
            ZammadImportRecordResult.Imported =>
                t with { Processed = t.Processed + 1, Imported = t.Imported + 1 },
            ZammadImportRecordResult.AlreadyImported =>
                t with { Processed = t.Processed + 1, AlreadyImported = t.AlreadyImported + 1 },
            _ => t with { Processed = t.Processed + 1, Failed = t.Failed + 1 },
        };
    }

    private static async Task<IReadOnlyList<DryRunMappedSnapshot>> LoadMappedDryRunRecordsAsync(
        NpgsqlDataSource ds, Guid dryRunId, CancellationToken ct)
    {
        const string sql = """
            SELECT id                       AS "RecordId",
                   zammad_ticket_id         AS "ZammadTicketId",
                   zammad_ticket_number     AS "ZammadTicketNumber",
                   zammad_ticket_title      AS "ZammadTicketTitle",
                   mapping::text            AS "MappingJson"
              FROM zammad_import_records
             WHERE run_id = @RunId AND result = 'mapped'
             ORDER BY id ASC
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MappedSnapshotRow>(new CommandDefinition(
            sql, new { RunId = dryRunId }, cancellationToken: ct));

        var list = new List<DryRunMappedSnapshot>();
        foreach (var r in rows)
        {
            var parsed = ParseSnapshot(r);
            if (parsed is null) continue;
            list.Add(parsed);
        }
        return list;
    }

    private static DryRunMappedSnapshot? ParseSnapshot(MappedSnapshotRow row)
    {
        if (string.IsNullOrWhiteSpace(row.MappingJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(row.MappingJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            Guid? contactId = TryGetGuid(root, "contactId");
            Guid? queueId = TryGetGuid(root, "queueId");
            Guid? statusId = TryGetGuid(root, "statusId");
            Guid? priorityId = TryGetGuid(root, "priorityId");
            if (contactId is null || queueId is null || statusId is null || priorityId is null)
                return null;
            return new DryRunMappedSnapshot(
                RecordId: row.RecordId,
                ZammadTicketId: row.ZammadTicketId,
                ZammadTicketNumber: row.ZammadTicketNumber,
                ZammadTicketTitle: row.ZammadTicketTitle,
                ContactId: contactId.Value,
                QueueId: queueId.Value,
                StatusId: statusId.Value,
                PriorityId: priorityId.Value,
                PendingTillUtc: TryGetUtcDateTime(root, "pendingTillUtc"),
                MappingJson: row.MappingJson);
        }
        catch (JsonException) { return null; }
    }

    private static Guid? TryGetGuid(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String when Guid.TryParse(prop.GetString(), out var g) => g,
            _ => null,
        };
    }

    private static DateTime? TryGetUtcDateTime(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTime.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var v)
            ? DateTime.SpecifyKind(v, DateTimeKind.Utc)
            : (DateTime?)null;
    }

    private static async Task InsertImportRecordAsync(
        NpgsqlDataSource ds,
        Guid runId,
        DryRunMappedSnapshot snapshot,
        ZammadImportWriteResult writeResult,
        IReadOnlyList<string> reasons,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO zammad_import_records (
                run_id, zammad_ticket_id, zammad_ticket_number, zammad_ticket_title,
                result, unresolved_reasons, mapping, would_create_ticket_id)
            VALUES (
                @RunId, @ZammadTicketId, @ZammadTicketNumber, @ZammadTicketTitle,
                @Result, @UnresolvedReasons, @Mapping::jsonb, @LocalTicketId)
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            ZammadTicketId = snapshot.ZammadTicketId,
            ZammadTicketNumber = snapshot.ZammadTicketNumber,
            ZammadTicketTitle = snapshot.ZammadTicketTitle,
            Result = writeResult.Result,
            UnresolvedReasons = reasons.ToArray(),
            Mapping = snapshot.MappingJson,
            LocalTicketId = writeResult.LocalTicketId,
        }, cancellationToken: ct));
    }

    private sealed record DryRunMappedSnapshot(
        Guid RecordId,
        long ZammadTicketId,
        string? ZammadTicketNumber,
        string? ZammadTicketTitle,
        Guid ContactId,
        Guid QueueId,
        Guid StatusId,
        Guid PriorityId,
        DateTime? PendingTillUtc,
        string MappingJson);

    private sealed class MappedSnapshotRow
    {
        public Guid RecordId { get; set; }
        public long ZammadTicketId { get; set; }
        public string? ZammadTicketNumber { get; set; }
        public string? ZammadTicketTitle { get; set; }
        public string? MappingJson { get; set; }
    }

    // ---- per-ticket resolver hot loop ---------------------------------

    private static async Task<ZammadImportTotals> ProcessTicketAsync(
        NpgsqlDataSource ds,
        IZammadTicketResolver resolver,
        ZammadMappingDictionary dict,
        Guid runId,
        long zammadTicketId,
        ZammadImportTotals totals,
        CancellationToken ct)
    {
        var resolved = await resolver.ResolveAsync(zammadTicketId, dict, ct);
        await InsertRecordAsync(
            ds, runId, zammadTicketId,
            resolved.ZammadTicketNumber,
            resolved.ZammadTicketTitle,
            resolved.Result,
            resolved.UnresolvedReasons,
            resolved.Mapping,
            ct);
        return BumpTotals(totals, resolved.Result);
    }

    private static ZammadImportTotals BumpTotals(ZammadImportTotals t, string result)
    {
        return result switch
        {
            ZammadImportRecordResult.Mapped =>
                t with { Processed = t.Processed + 1, Mapped = t.Mapped + 1 },
            ZammadImportRecordResult.SkippedNoContact =>
                t with { Processed = t.Processed + 1, SkippedNoContact = t.SkippedNoContact + 1 },
            ZammadImportRecordResult.SkippedNoGroupMapping =>
                t with { Processed = t.Processed + 1, SkippedNoGroupMapping = t.SkippedNoGroupMapping + 1 },
            ZammadImportRecordResult.SkippedNoStateMapping =>
                t with { Processed = t.Processed + 1, SkippedNoStateMapping = t.SkippedNoStateMapping + 1 },
            ZammadImportRecordResult.SkippedNoPriorityMapping =>
                t with { Processed = t.Processed + 1, SkippedNoPriorityMapping = t.SkippedNoPriorityMapping + 1 },
            _ => t with { Processed = t.Processed + 1, Failed = t.Failed + 1 },
        };
    }

    // ---- ticket-id resolution -----------------------------------------

    private async Task<IReadOnlyList<long>> ResolveTicketIdsAsync(
        ZammadImportSourceFilter? filter,
        IZammadApiClient zammad,
        int hardCap,
        CancellationToken ct)
    {
        if (filter is null) return Array.Empty<long>();

        // Explicit selection wins — the admin picked exact ticket ids in
        // the picker. No re-search.
        if (filter.TicketIds is { Count: > 0 })
        {
            return filter.TicketIds;
        }

        if (!filter.SelectAllMatching) return Array.Empty<long>();

        // Re-query the search with the persisted free-text + group/state
        // filters; page until we drain or hit the hard cap. Re-querying
        // (instead of trusting a snapshot) means a long-deferred run
        // sees the current Zammad state — usually what the admin wants
        // but worth noting in the run-detail page later.
        var ids = new List<long>();
        var perPage = 100;
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = new ZammadTicketSearchQuery(
                FreeText: filter.FreeText,
                GroupIds: filter.GroupIds ?? Array.Empty<long>(),
                StateIds: filter.StateIds ?? Array.Empty<long>(),
                Page: page,
                PerPage: perPage);
            var pageResult = await zammad.SearchTicketsAsync(query, ct);
            if (pageResult.Items.Count == 0) break;
            foreach (var it in pageResult.Items)
            {
                ids.Add(it.Id);
                if (ids.Count >= hardCap) return ids;
            }
            if (pageResult.Items.Count < perPage) break;
            page++;
        }
        return ids;
    }

    // ---- DB plumbing --------------------------------------------------

    private static async Task<(string Kind, string Status, ZammadImportSourceFilter? Filter, ZammadImportTotals Totals)?>
        LoadRunAsync(NpgsqlDataSource ds, Guid runId, CancellationToken ct)
    {
        const string sql = """
            SELECT kind                  AS "Kind",
                   status                AS "Status",
                   source_filter::text   AS "SourceFilterJson",
                   totals::text          AS "TotalsJson"
              FROM zammad_import_runs
             WHERE id = @RunId
             LIMIT 1
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LoadRunRow>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
        if (row is null) return null;

        ZammadImportSourceFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(row.SourceFilterJson))
        {
            try { filter = JsonSerializer.Deserialize<ZammadImportSourceFilter>(row.SourceFilterJson); }
            catch (JsonException) { filter = null; }
        }
        ZammadImportTotals totals = ZammadImportTotals.Empty(null);
        if (!string.IsNullOrWhiteSpace(row.TotalsJson))
        {
            try { totals = JsonSerializer.Deserialize<ZammadImportTotals>(row.TotalsJson) ?? totals; }
            catch (JsonException) { /* keep empty */ }
        }
        return (row.Kind, row.Status, filter, totals);
    }

    private static async Task<string?> GetRunStatusAsync(NpgsqlDataSource ds, Guid runId, CancellationToken ct)
    {
        const string sql = "SELECT status FROM zammad_import_runs WHERE id = @RunId";
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
    }

    private static async Task MarkRunStatusAsync(
        NpgsqlDataSource ds, Guid runId, string status, CancellationToken ct)
    {
        const string sql = "UPDATE zammad_import_runs SET status = @Status WHERE id = @RunId";
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { RunId = runId, Status = status }, cancellationToken: ct));
    }

    private static async Task PersistTotalsAsync(
        NpgsqlDataSource ds, Guid runId, ZammadImportTotals totals, CancellationToken ct)
    {
        const string sql = """
            UPDATE zammad_import_runs
               SET totals = @Totals::jsonb
             WHERE id = @RunId
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            Totals = JsonSerializer.Serialize(totals),
        }, cancellationToken: ct));
    }

    private static async Task CompleteRunAsync(
        NpgsqlDataSource ds, Guid runId, ZammadImportTotals totals, CancellationToken ct)
    {
        const string sql = """
            UPDATE zammad_import_runs
               SET status = 'completed',
                   finished_utc = now(),
                   totals = @Totals::jsonb
             WHERE id = @RunId
               AND status NOT IN ('cancelled','failed')
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            Totals = JsonSerializer.Serialize(totals),
        }, cancellationToken: ct));
    }

    private static async Task MarkRunFailedAsync(
        NpgsqlDataSource ds, Guid runId, string message, CancellationToken ct)
    {
        const string sql = """
            UPDATE zammad_import_runs
               SET status = 'failed',
                   finished_utc = now(),
                   error_message = @ErrorMessage
             WHERE id = @RunId
               AND status NOT IN ('completed','cancelled')
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { RunId = runId, ErrorMessage = message }, cancellationToken: ct));
    }

    private static async Task InsertRecordAsync(
        NpgsqlDataSource ds,
        Guid runId,
        long zammadTicketId,
        string? zammadTicketNumber,
        string? zammadTicketTitle,
        string result,
        IReadOnlyList<string> reasons,
        IReadOnlyDictionary<string, object?> snapshot,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO zammad_import_records (
                run_id, zammad_ticket_id, zammad_ticket_number, zammad_ticket_title,
                result, unresolved_reasons, mapping)
            VALUES (
                @RunId, @ZammadTicketId, @ZammadTicketNumber, @ZammadTicketTitle,
                @Result, @UnresolvedReasons, @Mapping::jsonb)
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            ZammadTicketId = zammadTicketId,
            ZammadTicketNumber = zammadTicketNumber,
            ZammadTicketTitle = zammadTicketTitle,
            Result = result,
            UnresolvedReasons = reasons.ToArray(),
            Mapping = JsonSerializer.Serialize(snapshot),
        }, cancellationToken: ct));
    }

    private async Task LogLifecycleAsync(
        IAuditLogger audit,
        string eventType,
        Guid runId,
        object payload,
        CancellationToken ct)
    {
        try
        {
            await audit.LogAsync(new AuditEvent(
                EventType: eventType,
                Actor: "zammad-worker",
                ActorRole: "System",
                Target: $"zammad_import_run:{runId}",
                ClientIp: null,
                UserAgent: null,
                Payload: payload), ct);
        }
        catch (Exception ex)
        {
            // Audit-log write must never crash the worker. Log + move on.
            _logger.LogWarning(ex,
                "ZammadDryRunWorker: failed to write {EventType} audit row for run {RunId}",
                eventType, runId);
        }
    }

    private sealed class LoadRunRow
    {
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? SourceFilterJson { get; set; }
        public string? TotalsJson { get; set; }
    }
}
