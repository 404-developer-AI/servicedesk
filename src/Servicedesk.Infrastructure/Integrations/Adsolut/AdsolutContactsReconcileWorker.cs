using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.28 — slow contacts-reconcile loop. Default cadence 24h, configurable
/// via <c>Adsolut.Sync.Contacts.ReconcileIntervalHours</c>. Walks every
/// Adsolut-linked SD company and re-fetches the full contacts list to
/// catch the two cases the fast delta-loop misses:
/// <list type="bullet">
/// <item><b>Active-flips</b> — Adsolut does not bump
/// <c>customer.lastModified</c> when a contact's <c>active</c> goes
/// true ↔ false, so the company never appears in the delta-set and the
/// flip is invisible to the fast loop.</item>
/// <item><b>Hard-deletes the fast loop missed</b> — these usually do bump
/// <c>customer.lastModified</c> and are caught by the fast pass's
/// reconcile-on-the-fly, but a tick that errors out before the contacts
/// stage leaves the deleted state un-mirrored. The slow loop heals it.</item>
/// </list>
/// Skipped silently when the integration isn't configured, no dossier is
/// selected, the connection is in <c>invalid_grant</c>, or both contacts
/// toggles are OFF.
public sealed class AdsolutContactsReconcileWorker : PeriodicWorker
{
    public AdsolutContactsReconcileWorker(
        IServiceProvider sp,
        ILogger<AdsolutContactsReconcileWorker> logger) : base(sp, logger)
    {
    }

    protected override string WorkerName => "adsolut-contacts-reconcile";
    // Stagger past the sync-worker stagger (45s) by another minute so a
    // tight cluster of startup writes doesn't all land in the same
    // instant, and so the very first tick happens AFTER the regular
    // sync has had a chance to populate state.
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(105);
    protected override TimeSpan FallbackInterval => TimeSpan.FromHours(24);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var hours = await settings.GetAsync<int>(SettingKeys.Adsolut.SyncContactsReconcileIntervalHours, ct);
        return new WorkerSchedule(TimeSpan.FromHours(Math.Max(1, hours)));
    }

    protected override async Task RunAsync(IServiceProvider sp, PerfWorkerRun run, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var secrets = sp.GetRequiredService<IProtectedSecretStore>();
        var connections = sp.GetRequiredService<IAdsolutConnectionStore>();
        var reconciler = sp.GetRequiredService<IAdsolutContactsReconciler>();
        var auditLog = sp.GetRequiredService<IIntegrationAuditLogger>();

        // Skip silently when the integration isn't configured.
        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var hasRefreshToken = await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        if (string.IsNullOrEmpty(clientId) || !hasSecret || !hasRefreshToken)
        {
            return;
        }

        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is null)
        {
            // No dossier picked yet — sync-worker already surfaces this
            // every tick; we don't need a duplicate warn-row from the
            // reconcile loop.
            return;
        }

        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            return;
        }

        var pullContactsUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsUpdate, ct);
        var pullContactsCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsCreate, ct);
        if (!pullContactsUpdate && !pullContactsCreate)
        {
            return;
        }

        var options = new AdsolutContactsSyncOptions(pullContactsUpdate, pullContactsCreate);
        var stopwatch = Stopwatch.StartNew();
        AdsolutContactsReconcileResult result;
        string? errorMessage = null;
        try
        {
            result = await reconciler.ReconcileAllAsync(options, ct);
        }
        catch (AdsolutApiException ex)
        {
            run.Fail(ex);
            errorMessage = ex.UpstreamErrorCode ?? ex.HttpStatus?.ToString() ?? "api_error";
            result = new AdsolutContactsReconcileResult();
            Logger.LogWarning(ex, "Adsolut contacts-reconcile tick failed mid-pass.");
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            errorMessage = "tick_exception";
            result = new AdsolutContactsReconcileResult();
            Logger.LogError(ex, "Adsolut contacts-reconcile tick threw an unexpected exception.");
        }
        stopwatch.Stop();
        run.AddItems(result.ContactsCreated + result.ContactsUpdated);

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ContactsReconcileTick,
            Outcome: errorMessage is null ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Warn,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds,
            ErrorCode: errorMessage,
            Payload: new
            {
                companiesScanned = result.CompaniesScanned,
                contactsSeen = result.ContactsSeen,
                contactsCreated = result.ContactsCreated,
                contactsUpdated = result.ContactsUpdated,
                contactsSkippedNoChange = result.ContactsSkippedNoChange,
                contactsSkippedLocalNewer = result.ContactsSkippedLocalNewer,
                contactsSkippedToggleOff = result.ContactsSkippedToggleOff,
                contactsSkippedNoEmail = result.ContactsSkippedNoEmail,
                contactsSkippedLinkConflict = result.ContactsSkippedLinkConflict,
                linksReconciled = result.LinksReconciled,
                durationMs = (int)stopwatch.ElapsedMilliseconds,
            }), ct);
    }
}
