using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations;

/// v0.0.25 — periodic healthcheck for outbound integrations. Each tick:
/// 1. resolves the current state of every configured integration (today
///    only Adsolut),
/// 2. on a long-running cycle, performs an active refresh probe so a
///    revoked refresh-token surfaces without an admin needing to click
///    "Test refresh" by hand,
/// 3. writes a row to <c>integration_audit</c> for the admin overview,
///    and
/// 4. pushes the resolved status string to admin-clients over SignalR so
///    the tile + detail page flip immediately instead of waiting for the
///    SPA's 30-second poll.
///
/// Skipped when the integration isn't configured (no client_id / no
/// secret) — no row, no SignalR push — so a vanilla install doesn't fill
/// the audit table with "not_configured" heartbeats.
public sealed class IntegrationsHealthcheckWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<IntegrationsHealthcheckWorker> _logger;

    /// Single-instance overlap guard. Same belt-and-braces pattern as
    /// <c>TriggerSchedulerWorker</c>: even though the loop runs ticks
    /// sequentially today, a future fan-out would silently break the
    /// "one upstream call per interval" invariant without this guard.
    /// 0 = idle, 1 = running.
    private int _running;

    /// Tracks the last status broadcast per integration so we only push a
    /// SignalR notification when the resolved state actually flipped.
    /// Without this every tick would re-broadcast the same state and
    /// the SPA would invalidate its query unnecessarily — wasteful and
    /// network-noisy on a healthy install. <c>null</c> = never broadcast,
    /// so the first tick after process start always pushes once.
    private readonly Dictionary<string, string?> _lastBroadcast = new(StringComparer.Ordinal);

    public IntegrationsHealthcheckWorker(
        IServiceProvider sp,
        ILogger<IntegrationsHealthcheckWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first tick so the worker doesn't race the database
        // bootstrapper or the secret-store warmup. 30s lines up with the
        // existing security-activity monitor's stagger.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 300;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                interval = Math.Max(60, await settings.GetAsync<int>(
                    SettingKeys.Integrations.HealthcheckIntervalSeconds, stoppingToken));

                if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    _logger.LogWarning(
                        "Integrations healthcheck tick skipped: previous tick still running. Increase {Setting} if this happens consistently.",
                        SettingKeys.Integrations.HealthcheckIntervalSeconds);
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
                _logger.LogError(ex, "Integrations healthcheck tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        await TickAdsolutAsync(sp, ct);
    }

    private async Task TickAdsolutAsync(IServiceProvider sp, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var secrets = sp.GetRequiredService<IProtectedSecretStore>();
        var connections = sp.GetRequiredService<IAdsolutConnectionStore>();
        var syncStateStore = sp.GetRequiredService<IAdsolutSyncStateStore>();
        var auth = sp.GetRequiredService<IAdsolutAuthService>();
        var auditLog = sp.GetRequiredService<IIntegrationAuditLogger>();
        var notifier = sp.GetRequiredService<IIntegrationStatusNotifier>();

        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var hasRefreshToken = await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);

        // Not configured = the install simply isn't using Adsolut. No row,
        // no SignalR push, no audit-table noise. The previous broadcast
        // (if any) stays in _lastBroadcast so a re-config later still
        // generates exactly one transition push.
        if (string.IsNullOrEmpty(clientId) || !hasSecret)
        {
            return;
        }

        var connection = await connections.GetAsync(ct);
        var activeProbeHours = Math.Max(1, await settings.GetAsync<int>(
            SettingKeys.Integrations.HealthcheckActiveProbeHours, ct));

        // Decide whether to perform an active refresh probe. Skip if:
        // — no refresh token yet (admin hasn't connected),
        // — the connection is already in a terminal "invalid_grant" state
        //   (re-probing only produces another invalid_grant; admin must
        //   reconnect),
        // — we refreshed within the active-probe window already.
        var shouldActiveProbe =
            hasRefreshToken
            && !IsTerminalRefreshError(connection?.LastRefreshError)
            && (connection?.LastRefreshedUtc is null
                || DateTime.UtcNow - connection.LastRefreshedUtc.Value >= TimeSpan.FromHours(activeProbeHours));

        IntegrationAuditOutcome outcome;
        string? errorCode = null;

        if (shouldActiveProbe)
        {
            try
            {
                await auth.RefreshAccessTokenAsync(source: "healthcheck", ct: ct);
                outcome = IntegrationAuditOutcome.Ok;
            }
            catch (AdsolutRefreshException ex)
            {
                errorCode = ex.UpstreamErrorCode ?? "refresh_failed";
                outcome = ex.RequiresReconnect
                    ? IntegrationAuditOutcome.Error
                    // Transient — Wolters Kluwer 5xx, network blip — the
                    // RT is still believed-good. Surface as Warn so the
                    // admin can see the bump without the Critical klaxon.
                    : IntegrationAuditOutcome.Warn;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adsolut healthcheck active probe threw an unexpected exception.");
                errorCode = "probe_exception";
                outcome = IntegrationAuditOutcome.Warn;
            }
        }
        else
        {
            // Passive read of cached state — outcome maps from the resolver
            // string after we compute it below.
            outcome = IntegrationAuditOutcome.Ok;
        }

        // Centralised resolver — reads the freshly-mutated connection state
        // (post-probe) AND the sync-state.LastError, so a tick_exception in
        // the sync worker downgrades the health to sync_failing instead of
        // the OAuth probe overwriting it back to connected.
        var resolvedState = await AdsolutStateResolver.ComputeAsync(
            settings, secrets, connections, syncStateStore, ct);

        // Map state → audit outcome for the passive path. The active-probe
        // path already set outcome from probe success; only override here
        // when the probe was Ok but the resolver downgraded for sync health.
        if (outcome == IntegrationAuditOutcome.Ok)
        {
            outcome = resolvedState switch
            {
                AdsolutStateResolver.RefreshFailed => IntegrationAuditOutcome.Error,
                AdsolutStateResolver.SyncFailing => IntegrationAuditOutcome.Warn,
                AdsolutStateResolver.NotConnected => IntegrationAuditOutcome.Warn,
                _ => IntegrationAuditOutcome.Ok,
            };
        }

        await auditLog.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.HealthcheckTick,
            Outcome: outcome,
            ErrorCode: errorCode,
            Payload: new
            {
                state = resolvedState,
                activeProbe = shouldActiveProbe,
                hasRefreshToken,
            }), ct);

        // Only push when the state actually flipped from the previous
        // broadcast — a healthy install ticking every 5 minutes does not
        // need to wake every admin's tab to say "still healthy". The
        // _lastBroadcast cache is per-process so a restart broadcasts once
        // on first tick, which is exactly the desired sanity-check.
        if (!_lastBroadcast.TryGetValue(AdsolutEventTypes.Integration, out var previous)
            || previous != resolvedState)
        {
            _lastBroadcast[AdsolutEventTypes.Integration] = resolvedState;
            await notifier.NotifyStatusChangedAsync(AdsolutEventTypes.Integration, resolvedState, ct);
        }
    }

    private static bool IsTerminalRefreshError(string? errorCode)
        => string.Equals(errorCode, "invalid_grant", StringComparison.Ordinal);
}
