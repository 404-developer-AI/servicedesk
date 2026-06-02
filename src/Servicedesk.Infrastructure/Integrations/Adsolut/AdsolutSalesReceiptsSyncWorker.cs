using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Periodic ERP SalesReceipts (verkoopbonnen) pull from the active Adsolut
/// administration into the local mirror that feeds the Timesheet → Adsolut
/// tab. Opt-in: ticks do real work only when <c>Adsolut.Erp.SalesReceipts.Enabled</c>
/// is on, the integration is connected, a dossier is active, and the
/// connection is not in a terminal invalid_grant state. Each tick:
/// <list type="number">
/// <item>Lists receipts via cursor pagination, always IncludeFinishedState=true
/// (invoiced/finished receipts are excluded by default and we want them all).</item>
/// <item>Applies the admin's status filter on our side (the ERP API has no
/// per-status query param), then fetches each kept receipt by-id (the list
/// view omits performance lines) and upserts header + line-sets.</item>
/// <item>Computes total_excl_vat = Σ product-line excl-VAT + Σ performance
/// invoiceTotal (there is no header total in the API).</item>
/// <item>Writes the delta cursor + counters, an integration_audit summary,
/// and a SignalR sync-completed ping.</item>
/// </list>
public sealed class AdsolutSalesReceiptsSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IAdsolutSalesReceiptsSyncSignal _signal;
    private readonly ILogger<AdsolutSalesReceiptsSyncWorker> _logger;

    private int _running;

    public AdsolutSalesReceiptsSyncWorker(
        IServiceProvider sp,
        IAdsolutSalesReceiptsSyncSignal signal,
        ILogger<AdsolutSalesReceiptsSyncWorker> logger)
    {
        _sp = sp;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the Companies sync worker (45s) so startup writes don't
        // all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpSalesReceiptsSyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(5, minutes) * 60;

                var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, stoppingToken);
                if (enabled)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                    {
                        _logger.LogWarning("Adsolut SalesReceipts tick skipped: previous tick still running.");
                    }
                    else
                    {
                        try { await TickAsync(scope.ServiceProvider, stoppingToken); }
                        finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adsolut SalesReceipts sync tick failed.");
            }

            await WaitForNextTickAsync(intervalSeconds, stoppingToken);
        }
    }

    private async Task WaitForNextTickAsync(int intervalSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(intervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            if (_signal.ConsumeRequest()) return;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            var slice = remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining;
            try { await Task.Delay(slice, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var secrets = sp.GetRequiredService<IProtectedSecretStore>();
        var connections = sp.GetRequiredService<IAdsolutConnectionStore>();
        var client = sp.GetRequiredService<IAdsolutSalesReceiptsClient>();
        var repo = sp.GetRequiredService<IAdsolutSalesReceiptRepository>();
        var auditLog = sp.GetRequiredService<IIntegrationAuditLogger>();
        var notifier = sp.GetRequiredService<IIntegrationStatusNotifier>();

        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var hasRefreshToken = await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        if (string.IsNullOrEmpty(clientId) || !hasSecret || !hasRefreshToken) return;

        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.ErpSalesReceiptsSyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "no_administration_selected",
                Payload: new { reason = "Admin must pick a dossier first." }), ct);
            return;
        }
        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.ErpSalesReceiptsSyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "invalid_grant",
                Payload: new { reason = "Refresh token revoked — admin must reconnect." }), ct);
            return;
        }

        var statusFilter = ParseStatusFilter(await settings.GetAsync<string>(SettingKeys.Adsolut.ErpSalesReceiptsStatusFilter, ct));

        var existingState = await repo.GetSyncStateAsync(ct);
        var tickStartUtc = DateTime.UtcNow;
        var modifiedSince = existingState?.LastDeltaSyncUtc is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : (DateTimeOffset?)null;
        var isFullSync = modifiedSince is null;

        var stopwatch = Stopwatch.StartNew();
        var seen = 0;
        var upserted = 0;
        var skippedStatus = 0;
        var failures = 0;
        string? errorMessage = null;
        var completed = false;

        // High-water mark for resumable progress. With 10K+ receipts a full
        // sync is many minutes of by-id calls; we checkpoint the delta cursor
        // periodically (and on a fatal mid-pass error) so a crash/restart
        // resumes near where it stopped instead of re-doing everything. Pages
        // arrive in lastModified order, so the max lastModified of fully
        // upserted receipts is a safe ModifiedSince to resume from.
        DateTime? highWater = existingState?.LastDeltaSyncUtc;
        var sinceCheckpoint = 0;

        async Task CheckpointAsync(string? error)
        {
            await repo.SaveSyncStateAsync(new AdsolutSalesReceiptSyncState
            {
                LastFullSyncUtc = existingState?.LastFullSyncUtc,
                LastDeltaSyncUtc = highWater,
                LastError = error,
                LastErrorUtc = error is null ? null : DateTime.UtcNow,
                ReceiptsSeen = seen,
                ReceiptsUpserted = upserted,
            }, CancellationToken.None);
        }

        try
        {
            const int PageSize = 200;
            const int CheckpointEvery = 250;
            string? cursor = null;
            var pageGuard = 0;
            do
            {
                var page = await client.ListPageAsync(administrationId, modifiedSince, cursor, PageSize, ct);
                foreach (var item in page.Items)
                {
                    seen++;
                    // Status filter applied our side — the ERP API has no
                    // per-status query parameter. Skip the by-id fetch entirely
                    // for deselected statuses to keep WK load minimal.
                    if (statusFilter.Count > 0 &&
                        (item.StateCode is null || !statusFilter.Contains(item.StateCode)))
                    {
                        skippedStatus++;
                        continue;
                    }

                    try
                    {
                        var full = await client.GetByIdAsync(administrationId, item.Id, ct);
                        if (full is null) continue;
                        var total = ComputeTotalExclVat(full);
                        await repo.UpsertAsync(full, total, ct);
                        upserted++;
                        // Advance the high-water mark only for rows we actually
                        // persisted, so a resume never skips an unprocessed row.
                        if (item.LastModified is { } lm && (highWater is null || lm.UtcDateTime > highWater))
                        {
                            highWater = lm.UtcDateTime;
                        }
                        if (++sinceCheckpoint >= CheckpointEvery)
                        {
                            sinceCheckpoint = 0;
                            await CheckpointAsync(null);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (AdsolutApiException ex) when (ex.HttpStatus == 429)
                    {
                        // Rate-limited: stop the pass cleanly. Progress is
                        // checkpointed; the next tick (or a manual "Sync now")
                        // resumes from the high-water mark.
                        errorMessage = "rate_limited";
                        _logger.LogWarning(ex, "Adsolut SalesReceipts hit 429 — pausing pass; will resume from checkpoint.");
                        await CheckpointAsync(errorMessage);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Per-receipt failure must not abort the whole pass —
                        // log, count, continue. The row keeps its previous
                        // mirrored state (or stays absent) until the next tick.
                        failures++;
                        _logger.LogWarning(ex, "Adsolut SalesReceipt {Id} fetch/upsert failed; continuing.", item.Id);
                    }
                }

                cursor = page.NextCursor;
                if (!page.HasNext || string.IsNullOrEmpty(cursor)) break;
            }
            while (++pageGuard <= 5000);

            completed = true;

            // Keep the mirror aligned with the admin's selection: when a
            // status filter is set, drop any previously-mirrored receipts
            // whose status is no longer selected (children cascade). Only on a
            // completed pass — never mid-sync, or we'd delete rows not yet
            // re-fetched.
            if (statusFilter.Count > 0)
            {
                await repo.DeleteReceiptsNotInStatusesAsync(statusFilter, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-pass — persist progress so a restart resumes.
            await CheckpointAsync(existingState?.LastError);
            throw;
        }
        catch (AdsolutApiException ex)
        {
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            _logger.LogWarning(ex, "Adsolut SalesReceipts tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            errorMessage = "tick_exception";
            _logger.LogError(ex, "Adsolut SalesReceipts tick threw an unexpected exception.");
        }
        stopwatch.Stop();

        var newState = new AdsolutSalesReceiptSyncState
        {
            // On a completed clean pass, advance to tickStartUtc so the next
            // delta also catches rows modified during the (long) run. On a
            // partial/failed pass, keep the high-water mark so we resume.
            LastFullSyncUtc = completed && errorMessage is null && isFullSync ? tickStartUtc : existingState?.LastFullSyncUtc,
            LastDeltaSyncUtc = completed && errorMessage is null ? tickStartUtc : highWater,
            LastError = errorMessage,
            LastErrorUtc = errorMessage is null ? null : DateTime.UtcNow,
            ReceiptsSeen = seen,
            ReceiptsUpserted = upserted,
        };
        await repo.SaveSyncStateAsync(newState, ct);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ErpSalesReceiptsSyncTick,
            Outcome: errorMessage is null ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds,
            ErrorCode: errorMessage,
            Payload: new
            {
                isFullSync,
                completed,
                administrationId,
                seen,
                upserted,
                skippedStatus,
                failures,
                statusFilter = statusFilter.Count == 0 ? "all" : string.Join(",", statusFilter),
                durationMs = (int)stopwatch.ElapsedMilliseconds,
                modifiedSince = modifiedSince?.UtcDateTime,
            }), ct);

        await notifier.NotifySyncCompletedAsync(AdsolutEventTypes.Integration, ct);
    }

    /// total_excl_vat = Σ product-line excl-VAT totals + Σ performance invoice
    /// totals. The API has no header total; this is the value the tab shows.
    public static decimal ComputeTotalExclVat(AdsolutSalesReceipt r)
    {
        decimal total = 0;
        foreach (var l in r.Lines) total += l.TotalExclVat ?? 0m;
        foreach (var p in r.Performances) total += p.InvoiceTotal ?? 0m;
        return total;
    }

    private static HashSet<string> ParseStatusFilter(string? raw) =>
        new((raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
