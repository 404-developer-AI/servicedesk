using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Periodic ERP Articles (artikels) pull from the active Adsolut administration
/// into the local mirror that feeds the Contract Articles list (Contracts →
/// Contract Articles). Opt-in: ticks do real work only when
/// <c>Adsolut.Erp.Articles.Enabled</c> is on, the integration is connected, a
/// dossier is active, and the connection is not in a terminal invalid_grant
/// state. Each tick lists articles via cursor pagination and upserts each
/// straight from the list page (the list view carries the full record, so there
/// is no per-article by-id fetch). The delta cursor + counters + an
/// integration_audit summary are written at the end, plus a SignalR
/// sync-completed ping. There is no purge — the mirror reflects the catalogue
/// as the delta sees it.
public sealed class AdsolutArticlesSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IAdsolutArticlesSyncSignal _signal;
    private readonly ILogger<AdsolutArticlesSyncWorker> _logger;

    private int _running;

    public AdsolutArticlesSyncWorker(
        IServiceProvider sp,
        IAdsolutArticlesSyncSignal signal,
        ILogger<AdsolutArticlesSyncWorker> logger)
    {
        _sp = sp;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the Companies (45s) + SalesReceipts (60s) + Orders (75s)
        // workers so startup writes don't all land at once.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpArticlesSyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(5, minutes) * 60;

                var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpArticlesEnabled, stoppingToken);
                if (enabled)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                    {
                        _logger.LogWarning("Adsolut Articles tick skipped: previous tick still running.");
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
                _logger.LogError(ex, "Adsolut Articles sync tick failed.");
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
        var client = sp.GetRequiredService<IAdsolutArticlesClient>();
        var repo = sp.GetRequiredService<IAdsolutArticleRepository>();
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
                EventType: AdsolutEventTypes.ErpArticlesSyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "no_administration_selected",
                Payload: new { reason = "Admin must pick a dossier first." }), ct);
            return;
        }
        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.ErpArticlesSyncTick,
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
        // upserted articles is a safe ModifiedSince to resume from after a
        // crash/restart mid-pass.
        DateTime? highWater = existingState?.LastDeltaSyncUtc;
        var sinceCheckpoint = 0;

        async Task CheckpointAsync(string? error)
        {
            await repo.SaveSyncStateAsync(new AdsolutArticleSyncState
            {
                LastFullSyncUtc = existingState?.LastFullSyncUtc,
                LastDeltaSyncUtc = highWater,
                LastError = error,
                LastErrorUtc = error is null ? null : DateTime.UtcNow,
                ArticlesSeen = seen,
                ArticlesUpserted = upserted,
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
                foreach (var article in page.Items)
                {
                    seen++;
                    try
                    {
                        await repo.UpsertAsync(article, ct);
                        upserted++;
                        if (article.AdsolutLastModified is { } lm && (highWater is null || lm.UtcDateTime > highWater))
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
                        // Per-article failure must not abort the whole pass — log,
                        // count, continue. The row keeps its previous mirrored
                        // state (or stays absent) until the next tick.
                        failures++;
                        _logger.LogWarning(ex, "Adsolut Article {Id} upsert failed; continuing.", article.Id);
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
            _logger.LogWarning(ex, "Adsolut Articles hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            _logger.LogWarning(ex, "Adsolut Articles tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            errorMessage = "tick_exception";
            _logger.LogError(ex, "Adsolut Articles tick threw an unexpected exception.");
        }

        stopwatch.Stop();

        var newState = new AdsolutArticleSyncState
        {
            LastFullSyncUtc = completed && errorMessage is null && isFullSync ? tickStartUtc : existingState?.LastFullSyncUtc,
            LastDeltaSyncUtc = completed && errorMessage is null ? tickStartUtc : highWater,
            LastError = errorMessage,
            LastErrorUtc = errorMessage is null ? null : DateTime.UtcNow,
            ArticlesSeen = seen,
            ArticlesUpserted = upserted,
        };
        await repo.SaveSyncStateAsync(newState, ct);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ErpArticlesSyncTick,
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
