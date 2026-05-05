using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
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
        var contactsClient = sp.GetRequiredService<IAdsolutContactsClient>();
        var contactUpserter = sp.GetRequiredService<IAdsolutContactUpserter>();
        var contactPusher = sp.GetRequiredService<IAdsolutContactPusher>();
        var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
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
        // v0.0.28 — Contacts pull toggles. Independent of the companies-pull
        // toggles: an admin can be opted in to pulling contact updates while
        // never accepting new contact rows from Adsolut, or vice versa.
        var pullContactsUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsUpdate, ct);
        var pullContactsCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsCreate, ct);
        // v0.0.29 — Contacts push toggles. Independent of the contacts-pull
        // toggles: an admin can opt in to pushing local edits to Adsolut
        // while never accepting inbound contact updates, or vice versa.
        var pushContactsUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPushContactsUpdate, ct);
        var pushContactsCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPushContactsCreate, ct);
        // Load the freemail blacklist once per tick (same source the
        // mail-ingest auto-linker uses, so the two paths can never disagree
        // on which domains count as freemail).
        var freemailBlacklist = await MailDomainBlacklist.LoadAsync(settings, _logger, ct);
        var options = new AdsolutSyncOptions(
            pullUpdate, pullCreate,
            LinkCompanyDomainsFromEmail: linkDomains,
            FreemailBlacklist: freemailBlacklist);
        var pushOptions = new AdsolutPushOptions(pushUpdate, pushCreate);
        var contactsOptions = new AdsolutContactsSyncOptions(pullContactsUpdate, pullContactsCreate);
        var contactsPushOptions = new AdsolutContactsPushOptions(pushContactsUpdate, pushContactsCreate);

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
            // v0.0.28 — collect Adsolut customer-UUIDs as we page through.
            // The contacts-pass uses these to look up SD companyIds in one
            // bulk SELECT instead of per-row round-trips, AND it stays
            // accurate even when companies-update toggle is OFF (in which
            // case the upserter doesn't bump adsolut_last_modified).
            var seenCustomerIds = new List<Guid>();
            await PullEndpointAsync(customers.ListCustomersAsync, administrationId, modifiedSince, options, upserter, counts, seenCustomerIds, ct);
            if (includeSuppliers)
            {
                // Suppliers branch is force-OFF in v0.0.28; this loop body
                // is unreachable in practice but kept symmetric with the
                // customers branch so the v0.0.x supplier-unlock is a
                // one-line code-flip.
                await PullEndpointAsync(customers.ListSuppliersAsync, administrationId, modifiedSince, options, upserter, counts, null, ct);
            }

            // v0.0.28 contacts pull-tak. Only fires when at least one of the
            // contacts toggles is on; otherwise short-circuits without
            // touching WK. The reconcile pass at the end of each company
            // catches hard-deletes (UUID disappeared from the fresh list)
            // even when the active flag itself wasn't flipped.
            if (contactsOptions.PullUpdateEnabled || contactsOptions.PullCreateEnabled)
            {
                await PullContactsAsync(
                    dataSource, contactsClient, contactUpserter,
                    administrationId, contactsOptions, seenCustomerIds, counts, ct);
            }

            // Note: PullContactsAsync swallows per-row exceptions so an
            // individual contact failure logs + counts but doesn't abort
            // the tick. The companies pull-tak doesn't have that yet —
            // a row exception there still propagates to the outer catch.

            // v0.0.27 push-tak — runs after the pull pass so any inbound
            // updates we just absorbed are visible (and protected by the
            // hash-no-op guard) before we evaluate drift candidates. Both
            // toggles default OFF — most ticks short-circuit at the
            // LoadCandidatesAsync gate without a SQL round-trip.
            if (pushOptions.PushUpdateEnabled || pushOptions.PushCreateEnabled)
            {
                await PushTakAsync(pusher, administrationId, pushOptions, counts, ct);
            }

            // v0.0.29 contacts push-tak — same ordering rationale as the
            // companies push: runs after the contacts-pull so any inbound
            // updates absorbed this tick are reflected in the per-link
            // hash before we evaluate outbound drift. Both toggles default
            // OFF; most ticks short-circuit before any SQL.
            if (contactsPushOptions.PushUpdateEnabled || contactsPushOptions.PushCreateEnabled)
            {
                await PushContactsAsync(contactPusher, administrationId, contactsPushOptions, counts, ct);
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
                contactsCustomersTouched = counts.ContactsCustomersTouched,
                contactsSeen = counts.ContactsSeen,
                contactsCreated = counts.ContactsCreated,
                contactsUpdated = counts.ContactsUpdated,
                contactsSkippedNoChange = counts.ContactsSkippedNoChange,
                contactsSkippedToggleOff = counts.ContactsSkippedToggleOff,
                contactsSkippedLocalNewer = counts.ContactsSkippedLocalNewer,
                contactsSkippedNoEmail = counts.ContactsSkippedNoEmail,
                contactsSkippedLinkConflict = counts.ContactsSkippedLinkConflict,
                contactsReconcileFlipped = counts.ContactsReconcileFlipped,
                contactsFailed = counts.ContactsFailed,
                contactsPushSeen = counts.ContactsPushSeen,
                contactsPushCreated = counts.ContactsPushCreated,
                contactsPushUpdated = counts.ContactsPushUpdated,
                contactsPushSkippedNoChange = counts.ContactsPushSkippedNoChange,
                contactsPushSkippedNoLocalChange = counts.ContactsPushSkippedNoLocalChange,
                contactsPushSkippedToggleOff = counts.ContactsPushSkippedToggleOff,
                contactsPushSkippedNoEmail = counts.ContactsPushSkippedNoEmail,
                contactsPushFailed = counts.ContactsPushFailed,
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

    /// v0.0.29 contacts push-tak — counterpart to <see cref="PushTakAsync"/>
    /// for contact_companies links. Per-row try/catch so one bad row
    /// doesn't crash the whole tick (lessons-learned from v0.0.28: the
    /// cursor must keep advancing, otherwise the install never makes
    /// forward progress).
    private async Task PushContactsAsync(
        IAdsolutContactPusher pusher,
        Guid administrationId,
        AdsolutContactsPushOptions options,
        AdsolutSyncCounters counts,
        CancellationToken ct)
    {
        const int PerTickCap = 200;
        var candidates = await pusher.LoadCandidatesAsync(options, PerTickCap, ct);
        foreach (var candidate in candidates)
        {
            counts.ContactsPushSeen++;
            try
            {
                var outcome = await pusher.PushOneAsync(administrationId, candidate, options, ct);
                switch (outcome)
                {
                    case AdsolutContactPushOutcome.Created:
                        counts.ContactsPushCreated++;
                        break;
                    case AdsolutContactPushOutcome.Updated:
                        counts.ContactsPushUpdated++;
                        break;
                    case AdsolutContactPushOutcome.SkippedNoChange:
                        counts.ContactsPushSkippedNoChange++;
                        break;
                    case AdsolutContactPushOutcome.SkippedNoLocalChange:
                        counts.ContactsPushSkippedNoLocalChange++;
                        break;
                    case AdsolutContactPushOutcome.SkippedUpdateToggleOff:
                    case AdsolutContactPushOutcome.SkippedCreateToggleOff:
                        counts.ContactsPushSkippedToggleOff++;
                        break;
                    case AdsolutContactPushOutcome.SkippedNoEmail:
                        counts.ContactsPushSkippedNoEmail++;
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AdsolutApiException ex)
            {
                counts.ContactsPushFailed++;
                _logger.LogInformation(
                    "Adsolut push of contact-link {LinkId} (contact {ContactId}, company {CompanyId}) failed: {Status} {Code}",
                    candidate.LinkId, candidate.ContactId, candidate.CompanyId,
                    ex.HttpStatus, ex.UpstreamErrorCode);
            }
            catch (Exception ex)
            {
                counts.ContactsPushFailed++;
                _logger.LogWarning(ex,
                    "Adsolut contact push threw an unexpected exception: link {LinkId}, contact {ContactId}, company {CompanyId}.",
                    candidate.LinkId, candidate.ContactId, candidate.CompanyId);
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
        List<Guid>? seenAdsolutIds,
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
                seenAdsolutIds?.Add(customer.Id);
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

    /// v0.0.28 contacts pull-tak. For each Adsolut customer the customers-tak
    /// just touched (whose customer.lastModified advanced this tick), fetch
    /// the full contacts list and upsert. The contacts sub-resource has no
    /// pagination and no ModifiedSince filter — every call returns the
    /// complete set, but each customer only gets called when its own stamp
    /// moved, so the load stays bounded by the delta.
    /// <para>
    /// Hard-delete catch-up rides on this same pass: <see cref="IAdsolutContactUpserter.ReconcileMissingLinksAsync"/>
    /// flips every link whose UUID is no longer in the fresh response set.
    /// Active-flip catch-up — where Adsolut does not bump
    /// <c>customer.lastModified</c> — is handled by the slower reconcile
    /// loop (separate worker, runs on
    /// <c>Adsolut.Sync.Contacts.ReconcileIntervalHours</c>).
    /// </para>
    private async Task PullContactsAsync(
        NpgsqlDataSource dataSource,
        IAdsolutContactsClient contactsClient,
        IAdsolutContactUpserter contactUpserter,
        Guid administrationId,
        AdsolutContactsSyncOptions contactsOptions,
        IReadOnlyCollection<Guid> seenAdsolutIds,
        AdsolutSyncCounters counts,
        CancellationToken ct)
    {
        if (seenAdsolutIds.Count == 0) return;

        // One bulk SELECT — pair every seen Adsolut UUID with its SD
        // companyId. Filtered to is_active=TRUE so soft-deleted companies
        // don't pull contacts (they're out of helpdesk scope).
        var pairs = await LoadCompanyPairsAsync(dataSource, seenAdsolutIds, ct);
        if (pairs.Count == 0) return;

        foreach (var pair in pairs)
        {
            var freshContacts = await contactsClient.ListCustomerContactsAsync(
                administrationId, pair.AdsolutId, ct);
            counts.ContactsCustomersTouched++;

            foreach (var inbound in freshContacts)
            {
                counts.ContactsSeen++;
                try
                {
                    var outcome = await contactUpserter.UpsertAsync(pair.CompanyId, inbound, contactsOptions, ct);
                    switch (outcome)
                    {
                        case AdsolutContactUpsertOutcome.Updated:
                            counts.ContactsUpdated++;
                            break;
                        case AdsolutContactUpsertOutcome.Created:
                            counts.ContactsCreated++;
                            break;
                        case AdsolutContactUpsertOutcome.SkippedNoChange:
                            counts.ContactsSkippedNoChange++;
                            break;
                        case AdsolutContactUpsertOutcome.SkippedLocalNewer:
                            counts.ContactsSkippedLocalNewer++;
                            break;
                        case AdsolutContactUpsertOutcome.SkippedUpdateToggleOff:
                        case AdsolutContactUpsertOutcome.SkippedCreateToggleOff:
                            counts.ContactsSkippedToggleOff++;
                            break;
                        case AdsolutContactUpsertOutcome.SkippedNoEmail:
                            counts.ContactsSkippedNoEmail++;
                            break;
                        case AdsolutContactUpsertOutcome.SkippedLinkCompanyMismatch:
                            counts.ContactsSkippedLinkConflict++;
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Per-row try/catch so one bad contact doesn't crash
                    // the whole tick (cursor stays put → next tick retries
                    // everything from before, the fast loop never makes
                    // forward progress). Log + count + continue. Pattern
                    // borrowed from PushTakAsync's per-candidate handling.
                    counts.ContactsFailed++;
                    _logger.LogWarning(ex,
                        "Adsolut contact upsert failed: company {CompanyId}, adsolut contact {AdsolutId}, email {Email}.",
                        pair.CompanyId, inbound.Id, inbound.Email);
                }
            }

            // Hard-delete catch-up: any link with a UUID that's no longer
            // in the fresh list flips inactive. Cheap when nothing changed
            // (UPDATE with WHERE filter selects zero rows).
            try
            {
                var freshIds = freshContacts.Select(c => c.Id).ToArray();
                counts.ContactsReconcileFlipped += await contactUpserter.ReconcileMissingLinksAsync(
                    pair.CompanyId, freshIds, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Adsolut contact reconcile-missing failed for company {CompanyId} ({AdsolutId}).",
                    pair.CompanyId, pair.AdsolutId);
            }
        }
    }

    private sealed class CompanyAdsolutPair
    {
        public Guid CompanyId { get; set; }
        public Guid AdsolutId { get; set; }
    }

    private static async Task<IReadOnlyList<CompanyAdsolutPair>> LoadCompanyPairsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<Guid> adsolutIds,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CompanyAdsolutPair>(new CommandDefinition(
            """
            SELECT id         AS CompanyId,
                   adsolut_id AS AdsolutId
            FROM companies
            WHERE is_active = TRUE
              AND adsolut_id = ANY(@Ids)
            """,
            new { Ids = adsolutIds.ToArray() },
            cancellationToken: ct));
        return rows.AsList();
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

        // Contacts pull-tak (v0.0.28).
        public int ContactsCustomersTouched;
        public int ContactsSeen;
        public int ContactsCreated;
        public int ContactsUpdated;
        public int ContactsSkippedNoChange;
        public int ContactsSkippedToggleOff;
        public int ContactsSkippedLocalNewer;
        public int ContactsSkippedNoEmail;
        public int ContactsSkippedLinkConflict;
        public int ContactsReconcileFlipped;
        public int ContactsFailed;

        // Contacts push-tak (v0.0.29).
        public int ContactsPushSeen;
        public int ContactsPushCreated;
        public int ContactsPushUpdated;
        public int ContactsPushSkippedNoChange;
        public int ContactsPushSkippedNoLocalChange;
        public int ContactsPushSkippedToggleOff;
        public int ContactsPushSkippedNoEmail;
        public int ContactsPushFailed;
    }
}
