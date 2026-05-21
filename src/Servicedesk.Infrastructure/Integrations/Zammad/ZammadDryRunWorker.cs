using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Settings;

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

    /// Maximum tickets walked per "Select all matching" dry-run. Hard
    /// stop to keep a runaway filter from chewing 100K tickets on the
    /// upstream. Setting-driven later; in fase 3 a generous 5K is plenty
    /// for the first migration test.
    private const int SelectAllMatchingHardCap = 5_000;

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
            var (status, filter, totals) = loaded.Value;

            if (status is "cancelled" or "completed" or "failed")
            {
                // Already terminal; nothing to do. Can happen when the
                // admin cancels before the worker picks up the message.
                return;
            }

            await MarkRunStatusAsync(dataSource, runId, "running", stoppingToken);
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

            // Resolve ticket-id list: explicit selection, or re-search.
            IReadOnlyList<long> ticketIds;
            try
            {
                ticketIds = await ResolveTicketIdsAsync(filter, zammad, stoppingToken);
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
                if (ids.Count >= SelectAllMatchingHardCap) return ids;
            }
            if (pageResult.Items.Count < perPage) break;
            page++;
        }
        return ids;
    }

    // ---- DB plumbing --------------------------------------------------

    private static async Task<(string Status, ZammadImportSourceFilter? Filter, ZammadImportTotals Totals)?>
        LoadRunAsync(NpgsqlDataSource ds, Guid runId, CancellationToken ct)
    {
        const string sql = """
            SELECT status                AS "Status",
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
        return (row.Status, filter, totals);
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
        public string Status { get; set; } = string.Empty;
        public string? SourceFilterJson { get; set; }
        public string? TotalsJson { get; set; }
    }
}
