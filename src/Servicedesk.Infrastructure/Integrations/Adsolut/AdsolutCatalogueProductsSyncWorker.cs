using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Periodic ERP CatalogueProducts pull from the active Adsolut administration
/// into the local mirror that feeds the Timesheet → Adsolut "VK Werkuren"
/// matching. Deliberately shares the SalesReceipts opt-in:
/// <c>Adsolut.Erp.SalesReceipts.Enabled</c> gates this worker too — the
/// catalogue only matters for the verkoopbon matching, so there is no separate
/// enable. Each tick lists products via cursor pagination and upserts each
/// straight from the list page (the list view carries the full record, so there
/// is no per-product by-id fetch); the admin-owned "counts as work hours" flag
/// is preserved across syncs. Incremental: after the first full import each tick
/// is a delta keyed on lastModified (?ModifiedSince=). The delta cursor +
/// counters + an integration_audit summary are written at the end, plus a
/// SignalR sync-completed ping. No purge.
public sealed class AdsolutCatalogueProductsSyncWorker : AdsolutSyncWorkerBase
{
    private readonly IAdsolutCatalogueProductsSyncSignal _signal;

    public AdsolutCatalogueProductsSyncWorker(
        IServiceProvider sp,
        IAdsolutCatalogueProductsSyncSignal signal,
        ILogger<AdsolutCatalogueProductsSyncWorker> logger) : base(sp, logger)
    {
        _signal = signal;
    }

    protected override string WorkerName => "adsolut-catalogue-products";
    // Stagger past the Companies (45s) + SalesReceipts (60s) + Orders (75s)
    // + Articles (90s) workers so startup writes don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(105);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(24);
    protected override IWorkerWakeSignal WakeSignal => _signal;
    protected override string IntervalMinutesKey => SettingKeys.Adsolut.ErpCatalogueProductsSyncIntervalMinutes;
    // Shares the SalesReceipts opt-in — the catalogue only feeds the
    // verkoopbon matching, so no separate enable flag.
    protected override string EnabledKey => SettingKeys.Adsolut.ErpSalesReceiptsEnabled;
    protected override string TickEventType => AdsolutEventTypes.ErpCatalogueProductsSyncTick;

    protected override async Task SyncAsync(IServiceProvider sp, Guid administrationId, PerfWorkerRun run, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IAdsolutCatalogueProductsClient>();
        var repo = sp.GetRequiredService<IAdsolutCatalogueProductRepository>();
        var auditLog = sp.GetRequiredService<IIntegrationAuditLogger>();
        var notifier = sp.GetRequiredService<IIntegrationStatusNotifier>();

        var existingState = await repo.GetSyncStateAsync(ct);
        var tickStartUtc = DateTime.UtcNow;
        var modifiedSince = existingState?.LastDeltaSyncUtc is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : (DateTimeOffset?)null;
        var isFullSync = modifiedSince is null;

        var stopwatch = Stopwatch.StartNew();
        var seen = 0;
        var upserted = 0;
        var failures = 0;
        string? errorMessage = null;
        var completed = false;

        // Pages arrive in lastModified order, so the max lastModified of fully
        // upserted products is a safe ModifiedSince to resume from after a
        // crash/restart mid-pass.
        DateTime? highWater = existingState?.LastDeltaSyncUtc;
        var sinceCheckpoint = 0;

        async Task CheckpointAsync(string? error)
        {
            await repo.SaveSyncStateAsync(new AdsolutCatalogueProductSyncState
            {
                LastFullSyncUtc = existingState?.LastFullSyncUtc,
                LastDeltaSyncUtc = highWater,
                LastError = error,
                LastErrorUtc = error is null ? null : DateTime.UtcNow,
                ProductsSeen = seen,
                ProductsUpserted = upserted,
            }, CancellationToken.None);
        }

        try
        {
            const int PageSize = 500;
            const int CheckpointEvery = 500;
            string? cursor = null;
            var pageGuard = 0;
            do
            {
                var page = await client.ListPageAsync(administrationId, modifiedSince, cursor, PageSize, ct);
                foreach (var product in page.Items)
                {
                    seen++;
                    try
                    {
                        await repo.UpsertAsync(product, ct);
                        upserted++;
                        if (product.AdsolutLastModified is { } lm && (highWater is null || lm.UtcDateTime > highWater))
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
                    catch (Exception ex)
                    {
                        // Per-product failure must not abort the whole pass — log,
                        // count, continue. The row keeps its previous mirrored
                        // state (or stays absent) until the next tick.
                        failures++;
                        Logger.LogWarning(ex, "Adsolut CatalogueProduct {Id} upsert failed; continuing.", product.Id);
                    }
                }

                cursor = page.NextCursor;
                if (!page.HasNext || string.IsNullOrEmpty(cursor)) break;
            }
            while (++pageGuard <= 5000);

            completed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CheckpointAsync(existingState?.LastError);
            throw;
        }
        catch (AdsolutApiException ex) when (ex.HttpStatus == 429)
        {
            run.Fail(ex);
            errorMessage = "rate_limited";
            Logger.LogWarning(ex, "Adsolut CatalogueProducts hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            run.Fail(ex);
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            Logger.LogWarning(ex, "Adsolut CatalogueProducts tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            errorMessage = "tick_exception";
            Logger.LogError(ex, "Adsolut CatalogueProducts tick threw an unexpected exception.");
        }

        stopwatch.Stop();
        run.AddItems(upserted);

        var newState = new AdsolutCatalogueProductSyncState
        {
            LastFullSyncUtc = completed && errorMessage is null && isFullSync ? tickStartUtc : existingState?.LastFullSyncUtc,
            LastDeltaSyncUtc = completed && errorMessage is null ? tickStartUtc : highWater,
            LastError = errorMessage,
            LastErrorUtc = errorMessage is null ? null : DateTime.UtcNow,
            ProductsSeen = seen,
            ProductsUpserted = upserted,
        };
        await repo.SaveSyncStateAsync(newState, ct);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ErpCatalogueProductsSyncTick,
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
                failures,
                durationMs = (int)stopwatch.ElapsedMilliseconds,
                modifiedSince = modifiedSince?.UtcDateTime,
            }), ct);

        await notifier.NotifySyncCompletedAsync(AdsolutEventTypes.Integration, ct);
    }
}
