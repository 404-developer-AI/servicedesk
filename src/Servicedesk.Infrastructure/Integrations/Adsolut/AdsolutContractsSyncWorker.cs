using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Periodic ERP Contracts (contracten) pull from the active Adsolut
/// administration into the local mirror that feeds the Contracts overview
/// (Contracts hub → Contracts overview). Opt-in: ticks do real work only when
/// <c>Adsolut.Erp.Contracts.Enabled</c> is on, the integration is connected, a
/// dossier is active, and the connection is not in a terminal invalid_grant
/// state. Each tick lists contracts via cursor pagination and upserts each
/// (header + article lines) straight from the list page — the Contracts list
/// view carries the full contract incl. lines, so there is no per-contract
/// by-id fetch. The delta cursor + counters + an integration_audit summary are
/// written at the end, plus a SignalR sync-completed ping. There is no
/// status-skip and no purge — the mirror always holds every status; the status
/// filter is display-only.
public sealed class AdsolutContractsSyncWorker : AdsolutSyncWorkerBase
{
    private readonly IAdsolutContractsSyncSignal _signal;

    public AdsolutContractsSyncWorker(
        IServiceProvider sp,
        IAdsolutContractsSyncSignal signal,
        ILogger<AdsolutContractsSyncWorker> logger) : base(sp, logger)
    {
        _signal = signal;
    }

    protected override string WorkerName => "adsolut-contracts";
    // Stagger past the Companies (45s) + SalesReceipts (60s) + Orders (75s)
    // + Articles (90s) workers so startup writes don't all land at once.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(105);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(1);
    protected override IWorkerWakeSignal WakeSignal => _signal;
    protected override string IntervalMinutesKey => SettingKeys.Adsolut.ErpContractsSyncIntervalMinutes;
    protected override string EnabledKey => SettingKeys.Adsolut.ErpContractsEnabled;
    protected override string TickEventType => AdsolutEventTypes.ErpContractsSyncTick;

    protected override async Task SyncAsync(IServiceProvider sp, Guid administrationId, PerfWorkerRun run, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IAdsolutContractsClient>();
        var repo = sp.GetRequiredService<IAdsolutContractRepository>();
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

        // Pages arrive in lastModified order (the cursor is the last row's
        // lastModified), so the max lastModified of fully upserted contracts is
        // a safe ModifiedSince to resume from after a crash/restart mid-pass.
        DateTime? highWater = existingState?.LastDeltaSyncUtc;
        var sinceCheckpoint = 0;

        async Task CheckpointAsync(string? error)
        {
            await repo.SaveSyncStateAsync(new AdsolutContractSyncState
            {
                LastFullSyncUtc = existingState?.LastFullSyncUtc,
                LastDeltaSyncUtc = highWater,
                LastError = error,
                LastErrorUtc = error is null ? null : DateTime.UtcNow,
                ContractsSeen = seen,
                ContractsUpserted = upserted,
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
                foreach (var contract in page.Items)
                {
                    seen++;
                    try
                    {
                        await repo.UpsertAsync(contract, ct);
                        upserted++;
                        if (contract.AdsolutLastModified is { } lm && (highWater is null || lm.UtcDateTime > highWater))
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
                        // Per-contract failure must not abort the whole pass —
                        // log, count, continue. The row keeps its previous
                        // mirrored state (or stays absent) until the next tick.
                        failures++;
                        Logger.LogWarning(ex, "Adsolut Contract {Id} upsert failed; continuing.", contract.Id);
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
            Logger.LogWarning(ex, "Adsolut Contracts hit 429 — pausing pass; will resume from checkpoint.");
            await CheckpointAsync(errorMessage);
            return;
        }
        catch (AdsolutApiException ex)
        {
            run.Fail(ex);
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            Logger.LogWarning(ex, "Adsolut Contracts tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            errorMessage = "tick_exception";
            Logger.LogError(ex, "Adsolut Contracts tick threw an unexpected exception.");
        }

        stopwatch.Stop();
        run.AddItems(upserted);

        var newState = new AdsolutContractSyncState
        {
            LastFullSyncUtc = completed && errorMessage is null && isFullSync ? tickStartUtc : existingState?.LastFullSyncUtc,
            LastDeltaSyncUtc = completed && errorMessage is null ? tickStartUtc : highWater,
            LastError = errorMessage,
            LastErrorUtc = errorMessage is null ? null : DateTime.UtcNow,
            ContractsSeen = seen,
            ContractsUpserted = upserted,
        };
        await repo.SaveSyncStateAsync(newState, ct);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ErpContractsSyncTick,
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

        // Resolve any contract customers we don't yet have a relation code for.
        // This bridges contracts (ERP customer GUID) → companies (relation code
        // == adsolut_number) and powers the Microsoft 365 matching list. Best
        // effort: failures are logged and retried next tick; it never fails the
        // contracts pass.
        await ResolveCustomersAsync(sp, administrationId, ct);

        await notifier.NotifySyncCompletedAsync(AdsolutEventTypes.Integration, ct);
    }

    /// Walk the contract customers with no cached relation code and fetch each
    /// via GET /erp/v1/adm/{adm}/Customers/{id}, caching id → code/name. One
    /// call per distinct unresolved customer (deduped), capped per tick. A 429
    /// stops the pass early — the remainder resolves on the next tick.
    private async Task ResolveCustomersAsync(IServiceProvider sp, Guid administrationId, CancellationToken ct)
    {
        const int MaxPerTick = 1000;
        var customersClient = sp.GetRequiredService<IAdsolutErpCustomersClient>();
        var customerRepo = sp.GetRequiredService<IAdsolutErpCustomerRepository>();
        var auditLog = sp.GetRequiredService<IIntegrationAuditLogger>();

        var stopwatch = Stopwatch.StartNew();
        var resolved = 0;
        var failures = 0;
        try
        {
            var pending = await customerRepo.GetUnresolvedContractCustomerIdsAsync(MaxPerTick, ct);
            foreach (var id in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var customer = await customersClient.GetByIdAsync(administrationId, id, ct);
                    // Cache even a null/empty result (as a NULL-code row) so we
                    // don't re-fetch a codeless customer every tick.
                    await customerRepo.UpsertAsync(customer ?? new AdsolutErpCustomer(id, null, null), ct);
                    resolved++;
                }
                catch (AdsolutApiException ex) when (ex.HttpStatus == 429)
                {
                    Logger.LogWarning(ex, "Adsolut ERP customer resolution hit 429 — pausing; resumes next tick.");
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures++;
                    Logger.LogWarning(ex, "Adsolut ERP customer {Id} resolution failed; continuing.", id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Adsolut ERP customer resolution step failed.");
        }
        stopwatch.Stop();

        if (resolved > 0 || failures > 0)
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.ErpCustomersGet,
                Outcome: failures == 0 ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                Payload: new { administrationId, resolved, failures }), CancellationToken.None);
        }
    }
}
