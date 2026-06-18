using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
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
public sealed class AdsolutCatalogueProductsSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IAdsolutCatalogueProductsSyncSignal _signal;
    private readonly ILogger<AdsolutCatalogueProductsSyncWorker> _logger;

    private int _running;

    public AdsolutCatalogueProductsSyncWorker(
        IServiceProvider sp,
        IAdsolutCatalogueProductsSyncSignal signal,
        ILogger<AdsolutCatalogueProductsSyncWorker> logger)
    {
        _sp = sp;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the Companies (45s) + SalesReceipts (60s) + Orders (75s)
        // + Articles (90s) workers so startup writes don't all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(105), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 24 * 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpCatalogueProductsSyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(5, minutes) * 60;

                // Shares the SalesReceipts opt-in — the catalogue only feeds the
                // verkoopbon matching, so no separate enable flag.
                var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, stoppingToken);
                if (enabled)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                    {
                        _logger.LogWarning("Adsolut CatalogueProducts tick skipped: previous tick still running.");
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
                _logger.LogError(ex, "Adsolut CatalogueProducts sync tick failed.");
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
        var client = sp.GetRequiredService<IAdsolutCatalogueProductsClient>();
        var repo = sp.GetRequiredService<IAdsolutCatalogueProductRepository>();
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
                EventType: AdsolutEventTypes.ErpCatalogueProductsSyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "no_administration_selected",
                Payload: new { reason = "Admin must pick a dossier first." }), ct);
            return;
        }
        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.ErpCatalogueProductsSyncTick,
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
                        _logger.LogWarning(ex, "Adsolut CatalogueProduct {Id} upsert failed; continuing.", product.Id);
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
            _logger.LogWarning(ex, "Adsolut CatalogueProducts hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            _logger.LogWarning(ex, "Adsolut CatalogueProducts tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            errorMessage = "tick_exception";
            _logger.LogError(ex, "Adsolut CatalogueProducts tick threw an unexpected exception.");
        }

        stopwatch.Stop();

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
