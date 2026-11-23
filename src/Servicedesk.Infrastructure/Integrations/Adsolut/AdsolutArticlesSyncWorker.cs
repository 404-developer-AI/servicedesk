using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Infrastructure.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Performance;
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
public sealed class AdsolutArticlesSyncWorker : AdsolutSyncWorkerBase
{
    private readonly IAdsolutArticlesSyncSignal _signal;

    public AdsolutArticlesSyncWorker(
        IServiceProvider sp,
        IAdsolutArticlesSyncSignal signal,
        ILogger<AdsolutArticlesSyncWorker> logger) : base(sp, logger)
    {
        _signal = signal;
    }

    protected override string WorkerName => "adsolut-articles";
    // Stagger past the Companies (45s) + SalesReceipts (60s) + Orders (75s)
    // workers so startup writes don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(90);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(1);
    protected override IWorkerWakeSignal WakeSignal => _signal;
    protected override string IntervalMinutesKey => SettingKeys.Adsolut.ErpArticlesSyncIntervalMinutes;
    protected override string EnabledKey => SettingKeys.Adsolut.ErpArticlesEnabled;
    protected override string TickEventType => AdsolutEventTypes.ErpArticlesSyncTick;

    protected override async Task SyncAsync(IServiceProvider sp, Guid administrationId, PerfWorkerRun run, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IAdsolutArticlesClient>();
        var repo = sp.GetRequiredService<IAdsolutArticleRepository>();
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
                        Logger.LogWarning(ex, "Adsolut Article {Id} upsert failed; continuing.", article.Id);
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
            Logger.LogWarning(ex, "Adsolut Articles hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            run.Fail(ex);
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            Logger.LogWarning(ex, "Adsolut Articles tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            errorMessage = "tick_exception";
            Logger.LogError(ex, "Adsolut Articles tick threw an unexpected exception.");
        }

        stopwatch.Stop();
        run.AddItems(upserted);

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
