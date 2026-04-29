using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.26 — periodic Companies pull from the active Adsolut administration.
/// Tick cadence is <c>Adsolut.Sync.IntervalMinutes</c> (floor 5). Skipped
/// when the integration is not configured, no dossier is selected, or the
/// connection is in a terminal <c>invalid_grant</c> state. Each tick:
/// <list type="number">
/// <item>Reads the cursor (last_delta_sync_utc) so a deltapass picks up
/// only rows that advanced upstream since the last successful tick.</item>
/// <item>Captures <c>tickStartUtc</c> at the start so a long-running
/// sync doesn't miss rows that landed mid-tick — the next tick re-pulls
/// from this checkpoint.</item>
/// <item>Pages through Customers (and Suppliers when toggled on),
/// running each row through <see cref="IAdsolutCompanyUpserter"/>.</item>
/// <item>Writes the cursor + counters back to <c>adsolut_sync_state</c>,
/// summary-row to <c>integration_audit</c>, and pushes
/// <c>IntegrationSyncCompleted</c> over SignalR.</item>
/// </list>
public sealed class AdsolutSyncWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IAdsolutSyncWorkerSignal _signal;
    private readonly ILogger<AdsolutSyncWorker> _logger;

    private int _running;

    public AdsolutSyncWorker(
        IServiceProvider sp,
        IAdsolutSyncWorkerSignal signal,
        ILogger<AdsolutSyncWorker> logger)
    {
        _sp = sp;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the healthcheck-worker stagger (30s) so a tight
        // cluster of startup writes doesn't all land in the same instant.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = 60 * 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var minutes = await settings.GetAsync<int>(SettingKeys.Adsolut.SyncIntervalMinutes, stoppingToken);
                intervalSeconds = Math.Max(5, minutes) * 60;

                if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    _logger.LogWarning(
                        "Adsolut sync tick skipped: previous tick still running. Increase {Setting} if this happens consistently.",
                        SettingKeys.Adsolut.SyncIntervalMinutes);
                }
                else
                {
                    try
                    {
                        await TickAsync(scope.ServiceProvider, stoppingToken);
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _running, 0);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adsolut sync tick failed.");
            }

            // Wait for either the interval to elapse or an admin-pressed
            // "Sync now" signal — whichever comes first.
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
            // Poll the signal at most every 2 seconds while waiting; long
            // enough to be cheap, short enough that "Sync now" feels live.
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
        var stateStore = sp.GetRequiredService<IAdsolutSyncStateStore>();
        var customers = sp.GetRequiredService<IAdsolutCustomersClient>();
        var upserter = sp.GetRequiredService<IAdsolutCompanyUpserter>();
        var pusher = sp.GetRequiredService<IAdsolutCompanyPusher>();
        var auditLog = sp.GetRequiredService<IIntegrationAuditLogger>();
        var notifier = sp.GetRequiredService<IIntegrationStatusNotifier>();

        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var hasRefreshToken = await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);

        // Skip silently when the integration isn't configured or the admin
        // hasn't connected yet — no audit-row, no notifier push. A vanilla
        // install must not produce sync.tick noise.
        if (string.IsNullOrEmpty(clientId) || !hasSecret || !hasRefreshToken)
        {
            return;
        }

        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            // Connected but no dossier picked yet. Surface as a warn-row
            // every tick so admins see the "you forgot to pick a dossier"
            // story in the integration audit.
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.SyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "no_administration_selected",
                Payload: new { reason = "Admin must pick a dossier on /settings/integrations/adsolut" }), ct);
            return;
        }

        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            await auditLog.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: AdsolutEventTypes.SyncTick,
                Outcome: IntegrationAuditOutcome.Warn,
                ErrorCode: "invalid_grant",
                Payload: new { reason = "Refresh token revoked — admin must reconnect." }), ct);
            return;
        }

        var pullUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullCompaniesUpdate, ct);
        var pullCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullCompaniesCreate, ct);
        // v0.0.27 — IncludeSuppliers is backend-force OFF until the v0.0.28
        // bidirectional-suppliers branch lands. Even if the setting row is
        // flipped to true (UI lock circumvented, SQL override, default
        // change in a fork), the worker ignores it. The setting stays in
        // place so the UI can show the toggle as "In development".
        _ = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncIncludeSuppliers, ct);
        var includeSuppliers = false;
        var pushUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.PushUpdateExistingCustomers, ct);
        var pushCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.PushCreateNewCustomers, ct);
        var linkDomains = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncLinkCompanyDomains, ct);
        // Load the freemail blacklist once per tick (same source the
        // mail-ingest auto-linker uses, so the two paths can never disagree
        // on which domains count as freemail).
        var freemailBlacklist = await MailDomainBlacklist.LoadAsync(settings, _logger, ct);
        var options = new AdsolutSyncOptions(
            pullUpdate, pullCreate,
            LinkCompanyDomainsFromEmail: linkDomains,
            FreemailBlacklist: freemailBlacklist);
        var pushOptions = new AdsolutPushOptions(pushUpdate, pushCreate);

        var existingState = await stateStore.GetAsync(ct);
        // Snap the cursor BEFORE making any upstream calls so a slow page
        // doesn't move the goalposts. Next tick re-pulls everything that
        // landed at or after this instant.
        var tickStartUtc = DateTime.UtcNow;
        var modifiedSince = existingState?.LastDeltaSyncUtc;
        var isFullSync = modifiedSince is null;

        var stopwatch = Stopwatch.StartNew();
        var counts = new AdsolutSyncCounters();
        string? errorMessage = null;

        try
        {
            await PullEndpointAsync(customers.ListCustomersAsync, administrationId, modifiedSince, options, upserter, counts, ct);
            if (includeSuppliers)
            {
                await PullEndpointAsync(customers.ListSuppliersAsync, administrationId, modifiedSince, options, upserter, counts, ct);
            }

            // v0.0.27 push-tak — runs after the pull pass so any inbound
            // updates we just absorbed are visible (and protected by the
            // hash-no-op guard) before we evaluate drift candidates. Both
            // toggles default OFF — most ticks short-circuit at the
            // LoadCandidatesAsync gate without a SQL round-trip.
            if (pushOptions.PushUpdateEnabled || pushOptions.PushCreateEnabled)
            {
                await PushTakAsync(pusher, administrationId, pushOptions, counts, ct);
            }
        }
        catch (AdsolutApiException ex)
        {
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            _logger.LogWarning(ex, "Adsolut sync tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            errorMessage = "tick_exception";
            _logger.LogError(ex, "Adsolut sync tick threw an unexpected exception.");
        }
        stopwatch.Stop();

        var newState = new AdsolutSyncState
        {
            // Only advance the delta cursor on a clean tick — partial
            // failures keep the old cursor so the next tick re-tries the
            // unprocessed slice instead of silently skipping it.
            LastFullSyncUtc = errorMessage is null && isFullSync ? tickStartUtc : existingState?.LastFullSyncUtc,
            LastDeltaSyncUtc = errorMessage is null ? tickStartUtc : existingState?.LastDeltaSyncUtc,
            LastError = errorMessage,
            LastErrorUtc = errorMessage is null ? null : DateTime.UtcNow,
            CompaniesSeen = counts.Seen,
            CompaniesUpserted = counts.Upserted,
            CompaniesSkippedLoserInConflict = counts.SkippedLocalNewer,
        };
        await stateStore.SaveAsync(newState, ct);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.SyncTick,
            Outcome: errorMessage is null ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds,
            ErrorCode: errorMessage,
            Payload: new
            {
                isFullSync,
                administrationId,
                seen = counts.Seen,
                created = counts.Created,
                updated = counts.Updated,
                skippedLocalNewer = counts.SkippedLocalNewer,
                skippedToggleOff = counts.SkippedToggleOff,
                skippedNoChange = counts.SkippedNoChange,
                pushSeen = counts.PushSeen,
                pushCreated = counts.PushCreated,
                pushUpdated = counts.PushUpdated,
                pushSkippedNoChange = counts.PushSkippedNoChange,
                pushSkippedNoLocalChange = counts.PushSkippedNoLocalChange,
                pushSkippedToggleOff = counts.PushSkippedToggleOff,
                pushSkippedMissingAdsolutNumber = counts.PushSkippedMissingAdsolutNumber,
                durationMs = (int)stopwatch.ElapsedMilliseconds,
                modifiedSince,
            }), ct);

        await notifier.NotifySyncCompletedAsync(AdsolutEventTypes.Integration, ct);

        // Push the resolved integration state so the dashboard health pill
        // and the integration tile flip without waiting on the next
        // healthcheck tick. The resolver reads the sync-state we just wrote
        // (LastError = errorMessage), so a tick_exception immediately
        // transitions the UI to sync_failing — and the next clean tick
        // transitions it back to connected.
        await notifier.NotifyStatusChangedAsync(
            AdsolutEventTypes.Integration,
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, stateStore, ct),
            ct);
    }

    /// v0.0.27 push-tak — read drift candidates from companies, run each
    /// through the pusher. Cap rows per tick to keep the WK API load
    /// predictable; a backlog from "first push after admin opt-in" gets
    /// chunked across ticks instead of a single 10K-row burst. Per-row
    /// AdsolutApiException is swallowed-with-audit so a bad row doesn't
    /// block the rest of the batch — the pusher already wrote the
    /// integration_audit row.
    private async Task PushTakAsync(
        IAdsolutCompanyPusher pusher,
        Guid administrationId,
        AdsolutPushOptions options,
        AdsolutSyncCounters counts,
        CancellationToken ct)
    {
        const int PerTickCap = 200;
        var candidates = await pusher.LoadCandidatesAsync(options, PerTickCap, ct);
        foreach (var candidate in candidates)
        {
            counts.PushSeen++;
            try
            {
                var outcome = await pusher.PushOneAsync(administrationId, candidate, options, ct);
                switch (outcome)
                {
                    case AdsolutPushOutcome.Created:
                        counts.PushCreated++;
                        break;
                    case AdsolutPushOutcome.Updated:
                        counts.PushUpdated++;
                        break;
                    case AdsolutPushOutcome.SkippedNoChange:
                        counts.PushSkippedNoChange++;
                        break;
                    case AdsolutPushOutcome.SkippedNoLocalChange:
                        counts.PushSkippedNoLocalChange++;
                        break;
                    case AdsolutPushOutcome.SkippedUpdateToggleOff:
                    case AdsolutPushOutcome.SkippedCreateToggleOff:
                        counts.PushSkippedToggleOff++;
                        break;
                    case AdsolutPushOutcome.SkippedMissingAdsolutNumber:
                        counts.PushSkippedMissingAdsolutNumber++;
                        break;
                }
            }
            catch (AdsolutApiException ex)
            {
                // Per-row failure already wrote a Warn/Error row via the
                // invoker. Log at info-level so the worker output stays
                // legible — we still continue with the next candidate.
                _logger.LogInformation(
                    "Adsolut push of company {CompanyId} failed: {Status} {Code}",
                    candidate.Id, ex.HttpStatus, ex.UpstreamErrorCode);
            }
        }
    }

    private static async Task PullEndpointAsync(
        Func<Guid, DateTimeOffset?, int, int, CancellationToken, Task<AdsolutPagedResult<AdsolutCustomer>>> list,
        Guid administrationId,
        DateTime? modifiedSince,
        AdsolutSyncOptions options,
        IAdsolutCompanyUpserter upserter,
        AdsolutSyncCounters counts,
        CancellationToken ct)
    {
        const int Limit = 100;
        var page = 1;
        var totalPages = 1;
        var since = modifiedSince is { } m ? new DateTimeOffset(m, TimeSpan.Zero) : (DateTimeOffset?)null;

        do
        {
            var pageResult = await list(administrationId, since, page, Limit, ct);
            totalPages = Math.Max(pageResult.TotalPages, page);

            foreach (var customer in pageResult.Items)
            {
                counts.Seen++;
                var outcome = await upserter.UpsertAsync(customer, options, ct);
                switch (outcome)
                {
                    case AdsolutUpsertOutcome.Updated:
                        counts.Updated++;
                        counts.Upserted++;
                        break;
                    case AdsolutUpsertOutcome.Created:
                        counts.Created++;
                        counts.Upserted++;
                        break;
                    case AdsolutUpsertOutcome.SkippedLocalNewer:
                        counts.SkippedLocalNewer++;
                        break;
                    case AdsolutUpsertOutcome.SkippedUpdateToggleOff:
                    case AdsolutUpsertOutcome.SkippedCreateToggleOff:
                        counts.SkippedToggleOff++;
                        break;
                    case AdsolutUpsertOutcome.SkippedNoChange:
                        counts.SkippedNoChange++;
                        break;
                }
            }

            // Defensive page-bound: respect both totalPages and a hard cap
            // so a malformed response can't loop us forever. 1000 pages at
            // 100 rows/page = 100K rows per endpoint per tick — well above
            // any single delta-sync we'll see.
            page++;
            if (page > 1000) break;
        }
        while (page <= totalPages);
    }

    private sealed class AdsolutSyncCounters
    {
        // Pull-tak.
        public int Seen;
        public int Upserted;
        public int Updated;
        public int Created;
        public int SkippedLocalNewer;
        public int SkippedToggleOff;
        public int SkippedNoChange;

        // Push-tak (v0.0.27).
        public int PushSeen;
        public int PushCreated;
        public int PushUpdated;
        public int PushSkippedNoChange;
        public int PushSkippedNoLocalChange;
        public int PushSkippedToggleOff;
        public int PushSkippedMissingAdsolutNumber;
    }
}
