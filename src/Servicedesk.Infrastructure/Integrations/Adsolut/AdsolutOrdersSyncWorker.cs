using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Periodic ERP Orders (bestellingen) pull from the active Adsolut
/// administration into the local mirror that feeds the Orders overview (navbar
/// → Assets → Orders). Opt-in: ticks do real work only when
/// <c>Adsolut.Erp.Orders.Enabled</c> is on, the integration is connected, a
/// dossier is active, and the connection is not in a terminal invalid_grant
/// state. Each tick:
/// <list type="number">
/// <item>Lists orders via cursor pagination, always IncludeFinishedState=true
/// (finished/closed orders are excluded by default and we mirror every
/// status — the status filter is display-only).</item>
/// <item>Upserts each order's header + line-set straight from the list page —
/// the OrderInfos list view already carries the full order incl. lines, so
/// there is no per-order by-id fetch.</item>
/// <item>Writes the delta cursor + counters, an integration_audit summary,
/// and a SignalR sync-completed ping.</item>
/// </list>
/// There is no status-skip and no purge — the mirror always holds every
/// status. Deselecting a status only hides it in the overview + search.
public sealed class AdsolutOrdersSyncWorker : AdsolutSyncWorkerBase
{
    private readonly IAdsolutOrdersSyncSignal _signal;

    public AdsolutOrdersSyncWorker(
        IServiceProvider sp,
        IAdsolutOrdersSyncSignal signal,
        ILogger<AdsolutOrdersSyncWorker> logger) : base(sp, logger)
    {
        _signal = signal;
    }

    protected override string WorkerName => "adsolut-orders";
    // Stagger past the Companies (45s) + SalesReceipts (60s) workers so
    // startup writes don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(75);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(1);
    protected override IWorkerWakeSignal WakeSignal => _signal;
    protected override string IntervalMinutesKey => SettingKeys.Adsolut.ErpOrdersSyncIntervalMinutes;
    protected override string EnabledKey => SettingKeys.Adsolut.ErpOrdersEnabled;
    protected override string TickEventType => AdsolutEventTypes.ErpOrdersSyncTick;

    protected override async Task SyncAsync(IServiceProvider sp, Guid administrationId, PerfWorkerRun run, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IAdsolutOrdersClient>();
        var repo = sp.GetRequiredService<IAdsolutOrderRepository>();
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
        // upserted orders is a safe ModifiedSince to resume from after a
        // crash/restart mid-pass.
        DateTime? highWater = existingState?.LastDeltaSyncUtc;
        var sinceCheckpoint = 0;

        async Task CheckpointAsync(string? error)
        {
            await repo.SaveSyncStateAsync(new AdsolutOrderSyncState
            {
                LastFullSyncUtc = existingState?.LastFullSyncUtc,
                LastDeltaSyncUtc = highWater,
                LastError = error,
                LastErrorUtc = error is null ? null : DateTime.UtcNow,
                OrdersSeen = seen,
                OrdersUpserted = upserted,
                // Preserve the supplier cursor — SaveSyncState writes every
                // column, so an orders-pass checkpoint must not wipe it.
                SupplierLastDeltaSyncUtc = existingState?.SupplierLastDeltaSyncUtc,
                SupplierOrdersSeen = existingState?.SupplierOrdersSeen ?? 0,
                SupplierOrdersUpserted = existingState?.SupplierOrdersUpserted ?? 0,
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
                foreach (var order in page.Items)
                {
                    seen++;
                    try
                    {
                        await repo.UpsertAsync(order, ct);
                        upserted++;
                        if (order.AdsolutLastModified is { } lm && (highWater is null || lm.UtcDateTime > highWater))
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
                        // Per-order failure must not abort the whole pass — log,
                        // count, continue. The row keeps its previous mirrored
                        // state (or stays absent) until the next tick.
                        failures++;
                        Logger.LogWarning(ex, "Adsolut Order {Id} upsert failed; continuing.", order.Id);
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
            Logger.LogWarning(ex, "Adsolut Orders hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            run.Fail(ex);
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            Logger.LogWarning(ex, "Adsolut Orders tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            errorMessage = "tick_exception";
            Logger.LogError(ex, "Adsolut Orders tick threw an unexpected exception.");
        }
        // Warehouses reference pass: a small full-list pull that keeps the
        // Stock/Location name mirror current. Runs every tick under the same
        // toggle; its own error handling so a hiccup never fails the tick.
        var warehousesClient = sp.GetRequiredService<IAdsolutWarehousesClient>();
        var warehouses = await RunWarehousesPassAsync(warehousesClient, repo, administrationId, ct);

        // Second pass: supplier orders (bestellingen). Independent delta cursor;
        // its own error handling so an orders success isn't undone by a supplier
        // hiccup. Runs in the same tick under the same toggle.
        var supplierClient = sp.GetRequiredService<IAdsolutSupplierOrdersClient>();
        var supplierSince = existingState?.SupplierLastDeltaSyncUtc;
        var supplier = await RunSupplierPassAsync(supplierClient, repo, administrationId, supplierSince, ct);

        stopwatch.Stop();
        run.AddItems(upserted + supplier.Upserted);

        var newState = new AdsolutOrderSyncState
        {
            LastFullSyncUtc = completed && errorMessage is null && isFullSync ? tickStartUtc : existingState?.LastFullSyncUtc,
            LastDeltaSyncUtc = completed && errorMessage is null ? tickStartUtc : highWater,
            LastError = errorMessage ?? supplier.Error,
            LastErrorUtc = (errorMessage ?? supplier.Error) is null ? null : DateTime.UtcNow,
            OrdersSeen = seen,
            OrdersUpserted = upserted,
            // On a clean supplier pass advance the cursor to tickStartUtc;
            // otherwise keep the high-water (or the previous cursor).
            SupplierLastDeltaSyncUtc = supplier.Error is null ? tickStartUtc : (supplier.HighWater ?? existingState?.SupplierLastDeltaSyncUtc),
            SupplierOrdersSeen = supplier.Seen,
            SupplierOrdersUpserted = supplier.Upserted,
        };
        await repo.SaveSyncStateAsync(newState, ct);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ErpOrdersSyncTick,
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
                supplierSeen = supplier.Seen,
                supplierUpserted = supplier.Upserted,
                supplierError = supplier.Error,
                warehousesSeen = warehouses.Seen,
                warehousesError = warehouses.Error,
                durationMs = (int)stopwatch.ElapsedMilliseconds,
                modifiedSince = modifiedSince?.UtcDateTime,
            }), ct);

        await notifier.NotifySyncCompletedAsync(AdsolutEventTypes.Integration, ct);
    }

    private readonly record struct SupplierPassResult(int Seen, int Upserted, DateTime? HighWater, string? Error);
    private readonly record struct WarehousesPassResult(int Seen, string? Error);

    /// One Warehouses reference pass: cursor through the (small) Warehouses list
    /// and replace the mirror wholesale. No delta cursor — the list is tiny and
    /// the mirror only needs to be current, not incremental. Own try/catch so a
    /// failure here never fails the orders/supplier passes.
    private async Task<WarehousesPassResult> RunWarehousesPassAsync(
        IAdsolutWarehousesClient client,
        IAdsolutOrderRepository repo,
        Guid administrationId,
        CancellationToken ct)
    {
        try
        {
            const int PageSize = 200;
            var all = new List<AdsolutWarehouse>();
            string? cursor = null;
            var pageGuard = 0;
            do
            {
                var page = await client.ListPageAsync(administrationId, cursor, PageSize, ct);
                all.AddRange(page.Items);
                cursor = page.NextCursor;
                if (!page.HasNext || string.IsNullOrEmpty(cursor)) break;
            }
            while (++pageGuard <= 1000);

            await repo.UpsertWarehousesAsync(all, ct);
            return new WarehousesPassResult(all.Count, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AdsolutApiException ex) when (ex.HttpStatus == 429)
        {
            Logger.LogWarning(ex, "Adsolut Warehouses hit 429 — skipping warehouse refresh this tick.");
            return new WarehousesPassResult(0, "rate_limited");
        }
        catch (AdsolutApiException ex)
        {
            Logger.LogWarning(ex, "Adsolut Warehouses pass failed.");
            return new WarehousesPassResult(0, ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Adsolut Warehouses pass threw an unexpected exception.");
            return new WarehousesPassResult(0, "warehouses_pass_exception");
        }
    }

    /// One supplier-orders (bestellingen) pass: cursor through SupplierOrderInfos
    /// (always IncludeReceivedState=true, ModifiedSince delta), upsert each.
    /// Per-order try/catch + a clean 429 pause. Returns counters + a resumable
    /// high-water mark so the caller can persist the cursor.
    private async Task<SupplierPassResult> RunSupplierPassAsync(
        IAdsolutSupplierOrdersClient client,
        IAdsolutOrderRepository repo,
        Guid administrationId,
        DateTime? supplierSince,
        CancellationToken ct)
    {
        var since = supplierSince is { } s ? new DateTimeOffset(s, TimeSpan.Zero) : (DateTimeOffset?)null;
        DateTime? highWater = supplierSince;
        var seen = 0;
        var upserted = 0;

        try
        {
            const int PageSize = 200;
            string? cursor = null;
            var pageGuard = 0;
            do
            {
                var page = await client.ListPageAsync(administrationId, since, cursor, PageSize, ct);
                foreach (var order in page.Items)
                {
                    seen++;
                    try
                    {
                        await repo.UpsertSupplierOrderAsync(order, ct);
                        upserted++;
                        if (order.AdsolutLastModified is { } lm && (highWater is null || lm.UtcDateTime > highWater))
                        {
                            highWater = lm.UtcDateTime;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Adsolut SupplierOrder {Id} upsert failed; continuing.", order.Id);
                    }
                }

                cursor = page.NextCursor;
                if (!page.HasNext || string.IsNullOrEmpty(cursor)) break;
            }
            while (++pageGuard <= 5000);

            return new SupplierPassResult(seen, upserted, highWater, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AdsolutApiException ex) when (ex.HttpStatus == 429)
        {
            Logger.LogWarning(ex, "Adsolut SupplierOrders hit 429 — pausing supplier pass; resumes next tick.");
            return new SupplierPassResult(seen, upserted, highWater, "rate_limited");
        }
        catch (AdsolutApiException ex)
        {
            Logger.LogWarning(ex, "Adsolut SupplierOrders pass failed.");
            return new SupplierPassResult(seen, upserted, highWater, ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Adsolut SupplierOrders pass threw an unexpected exception.");
            return new SupplierPassResult(seen, upserted, highWater, "supplier_pass_exception");
        }
    }
}
