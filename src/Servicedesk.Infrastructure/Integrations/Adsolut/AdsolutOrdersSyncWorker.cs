using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
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
public sealed class AdsolutOrdersSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IAdsolutOrdersSyncSignal _signal;
    private readonly ILogger<AdsolutOrdersSyncWorker> _logger;

    private int _running;

    public AdsolutOrdersSyncWorker(
        IServiceProvider sp,
        IAdsolutOrdersSyncSignal signal,
        ILogger<AdsolutOrdersSyncWorker> logger)
    {
        _sp = sp;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the Companies (45s) + SalesReceipts (60s) workers so
        // startup writes don't all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(75), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpOrdersSyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(5, minutes) * 60;

                var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpOrdersEnabled, stoppingToken);
                if (enabled)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                    {
                        _logger.LogWarning("Adsolut Orders tick skipped: previous tick still running.");
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
                _logger.LogError(ex, "Adsolut Orders sync tick failed.");
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
        var client = sp.GetRequiredService<IAdsolutOrdersClient>();
        var repo = sp.GetRequiredService<IAdsolutOrderRepository>();
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
                EventType: AdsolutEventTypes.ErpOrdersSyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "no_administration_selected",
                Payload: new { reason = "Admin must pick a dossier first." }), ct);
            return;
        }
        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.ErpOrdersSyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "invalid_grant",
                Payload: new { reason = "Refresh token revoked — admin must reconnect." }), ct);
            return;
        }

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
                        _logger.LogWarning(ex, "Adsolut Order {Id} upsert failed; continuing.", order.Id);
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
            errorMessage = "rate_limited";
            _logger.LogWarning(ex, "Adsolut Orders hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            _logger.LogWarning(ex, "Adsolut Orders tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            errorMessage = "tick_exception";
            _logger.LogError(ex, "Adsolut Orders tick threw an unexpected exception.");
        }
        // Second pass: supplier orders (bestellingen). Independent delta cursor;
        // its own error handling so an orders success isn't undone by a supplier
        // hiccup. Runs in the same tick under the same toggle.
        var supplierClient = sp.GetRequiredService<IAdsolutSupplierOrdersClient>();
        var supplierSince = existingState?.SupplierLastDeltaSyncUtc;
        var supplier = await RunSupplierPassAsync(supplierClient, repo, administrationId, supplierSince, ct);

        stopwatch.Stop();

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
                durationMs = (int)stopwatch.ElapsedMilliseconds,
                modifiedSince = modifiedSince?.UtcDateTime,
            }), ct);

        await notifier.NotifySyncCompletedAsync(AdsolutEventTypes.Integration, ct);
    }

    private readonly record struct SupplierPassResult(int Seen, int Upserted, DateTime? HighWater, string? Error);

    /// One supplier-orders (bestellingen) pass: cursor through SupplierOrderInfos
    /// (always IncludeFinishedState=true, ModifiedSince delta), upsert each.
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
                        _logger.LogWarning(ex, "Adsolut SupplierOrder {Id} upsert failed; continuing.", order.Id);
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
            _logger.LogWarning(ex, "Adsolut SupplierOrders hit 429 — pausing supplier pass; resumes next tick.");
            return new SupplierPassResult(seen, upserted, highWater, "rate_limited");
        }
        catch (AdsolutApiException ex)
        {
            _logger.LogWarning(ex, "Adsolut SupplierOrders pass failed.");
            return new SupplierPassResult(seen, upserted, highWater, ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adsolut SupplierOrders pass threw an unexpected exception.");
            return new SupplierPassResult(seen, upserted, highWater, "supplier_pass_exception");
        }
    }
}
