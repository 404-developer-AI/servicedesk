using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// The shared shell of the Adsolut ERP mirror workers (ROADMAP §2 C1):
/// interval in minutes (floor 5) + an opt-in switch from settings, an admin's
/// *Sync now* signal, and the connection preconditions every pull needs —
/// credentials present (silently idle otherwise), a dossier picked and no
/// terminal <c>invalid_grant</c> (both logged as a Warn tick in the
/// integration audit so the admin sees why nothing happens). A subclass only
/// says which settings and audit event it uses and how one pass pulls.
public abstract class AdsolutSyncWorkerBase : PeriodicWorker
{
    protected AdsolutSyncWorkerBase(IServiceProvider services, ILogger logger) : base(services, logger) { }

    /// Setting with the interval in minutes (floor 5).
    protected abstract string IntervalMinutesKey { get; }

    /// Setting with the opt-in switch.
    protected abstract string EnabledKey { get; }

    /// Integration-audit event type of one tick (also used for the Warn
    /// rows when a precondition is not met).
    protected abstract string TickEventType { get; }

    /// One pull against the active administration (dossier).
    protected abstract Task SyncAsync(IServiceProvider services, Guid administrationId, PerfWorkerRun run, CancellationToken ct);

    protected sealed override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var minutes = await settings.GetAsync<int>(IntervalMinutesKey, ct);
        var enabled = await settings.GetAsync<bool>(EnabledKey, ct);
        return new WorkerSchedule(TimeSpan.FromMinutes(Math.Max(5, minutes)), enabled);
    }

    protected sealed override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var secrets = services.GetRequiredService<IProtectedSecretStore>();
        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(clientId)
            || !await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct)
            || !await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct))
            return;

        var connection = await services.GetRequiredService<IAdsolutConnectionStore>().GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            await WarnAsync(services, "no_administration_selected", "Admin must pick a dossier first.", ct);
            return;
        }
        if (string.Equals(connection.LastRefreshError, "invalid_grant", StringComparison.Ordinal))
        {
            await WarnAsync(services, "invalid_grant", "Refresh token revoked — admin must reconnect.", ct);
            return;
        }

        await SyncAsync(services, administrationId, run, ct);
    }

    private Task WarnAsync(IServiceProvider services, string code, string reason, CancellationToken ct) =>
        services.GetRequiredService<IIntegrationAuditLogger>().LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: TickEventType,
            Outcome: IntegrationAuditOutcome.Warn,
            ErrorCode: code,
            Payload: new { reason }), ct);
}
