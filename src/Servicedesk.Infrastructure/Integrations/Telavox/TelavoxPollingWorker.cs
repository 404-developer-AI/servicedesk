using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// v0.0.34 commit C — per-agent CAPI polling worker. Each tick:
/// <list type="number">
/// <item>Reads <see cref="SettingKeys.Telavox.Enabled"/> + partner-token
/// + customer-id. Any of them missing → silent skip (vanilla install
/// does not fire empty heartbeats).</item>
/// <item>Evaluates the configured working-hours window. Outside the
/// window the worker either fully sleeps
/// (<see cref="SettingKeys.Telavox.PollWindowSleepOutsideHours"/> = true,
/// default) or falls back to a slow 60s tick so very-late calls still
/// surface a popup.</item>
/// <item>Reads <c>telavox_agent_links</c> and, for each linked agent,
/// loads the per-agent CAPI token from <see cref="IProtectedSecretStore"/>
/// and calls <see cref="ITelavoxApiClient.GetCurrentCallAsync"/>.</item>
/// <item>Runs the transition through <see cref="TelavoxCallTransition.Evaluate"/>.
/// When the configured trigger fires, pushes a <c>TelavoxCall</c> event
/// to the matched agent via <see cref="ITelavoxCallNotifier"/>.</item>
/// <item>Writes the new baseline to <c>telavox_call_state</c> and bumps
/// the per-link operational counters (last_poll_utc, last_poll_error,
/// consecutive_errors) so the integration page can flag stuck agents
/// without a second HTTP round-trip.</item>
/// </list>
///
/// Per-call audit rows happen inside <see cref="TelavoxApiClient"/>; this
/// worker writes one additional tick-summary row to
/// <c>integration_audit</c> at the end so an admin can see "polled N
/// agents in M ms" without summing per-call rows.
public sealed class TelavoxPollingWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<TelavoxPollingWorker> _logger;

    /// Single-instance overlap guard. A slow Telavox API response could in
    /// theory blow past one tick-interval; the same belt-and-braces pattern
    /// as the other workers (Adsolut sync, healthcheck) keeps the
    /// "one upstream call per agent per interval" invariant safe. Relies on
    /// the hosted-service singleton lifetime — every <c>AddHostedService</c>
    /// registration is process-singleton by design, so this field is
    /// process-wide. 0 = idle, 1 = running.
    private int _running;

    // Scale ceiling: at the default 2s PollIntervalSeconds and ~200ms
    // Telavox latency the per-tick budget is exhausted around 10 linked
    // agents. Beyond ~15 the overlap-guard above will start logging skip
    // warnings — the fix at that scale is to switch the per-agent loop
    // below to Task.WhenAll with a small SemaphoreSlim. Kept sequential
    // for v0.0.34 first-release so the per-tick audit row stays a single
    // deterministic timeline and the ordering is easy to reason about.

    public TelavoxPollingWorker(
        IServiceProvider sp,
        ILogger<TelavoxPollingWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger past the other startup workers (healthcheck 30s,
        // Adsolut sync 45s) so secret-store warmup + bootstrapper writes
        // are done before the first Telavox call goes out.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelaySeconds = 2;
            try
            {
                using var scope = _sp.CreateScope();
                nextDelaySeconds = await TickOrSkipAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telavox polling tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(nextDelaySeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// Cheap-skip back-off when the integration is off or unconfigured. A
    /// vanilla install never opts in, so we'd otherwise hit settings +
    /// protected_secrets ~2 RPS forever for nothing. 60s aligns with the
    /// healthcheck cadence, so a flip to <c>Enabled=true</c> still picks
    /// up within one minute.
    private const int DisabledSkipDelaySeconds = 60;

    /// Decides whether to do a tick and what the next interval should be.
    /// Returns the seconds to wait before the next attempt. Three regimes:
    /// <list type="bullet">
    /// <item>Active and in-window → configured PollIntervalSeconds.</item>
    /// <item>Active but out-of-window slow-tick → max(configured, 60s).</item>
    /// <item>Disabled or unconfigured → <see cref="DisabledSkipDelaySeconds"/>
    /// so the settings-table isn't hammered for no benefit.</item>
    /// </list>
    private async Task<int> TickOrSkipAsync(IServiceProvider sp, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var secrets = sp.GetRequiredService<IProtectedSecretStore>();

        var enabled = await settings.GetAsync<bool>(SettingKeys.Telavox.Enabled, ct);
        if (!enabled) return DisabledSkipDelaySeconds;

        var partnerToken = await secrets.HasAsync(ProtectedSecretKeys.TelavoxPartnerToken, ct);
        if (!partnerToken) return DisabledSkipDelaySeconds;

        var customerId = (await settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct)
            ?? string.Empty).Trim();
        if (customerId.Length == 0) return DisabledSkipDelaySeconds;

        // Only read the poll-interval setting once we know we're actually
        // going to poll — saves one DB hit in the disabled-skip path.
        var pollInterval = Math.Clamp(
            await settings.GetAsync<int>(SettingKeys.Telavox.PollIntervalSeconds, ct),
            1, 30);

        // Working-hours window. We evaluate in the configured display TZ
        // so an admin in Brussels running a UTC-host server sees their
        // "08:00–18:00" window line up with their local clock.
        var tzId = await settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
        var tz = ResolveTimeZone(tzId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var windowStart = await settings.GetAsync<string>(SettingKeys.Telavox.PollWindowStart, ct);
        var windowEnd = await settings.GetAsync<string>(SettingKeys.Telavox.PollWindowEnd, ct);
        var windowDays = await settings.GetAsync<string>(SettingKeys.Telavox.PollWindowDays, ct);
        var inWindow = TelavoxPollWindow.IsInPollWindow(nowLocal, windowStart, windowEnd, windowDays);
        if (!inWindow)
        {
            var sleepFully = await settings.GetAsync<bool>(
                SettingKeys.Telavox.PollWindowSleepOutsideHours, ct);
            if (sleepFully) return pollInterval; // honour the cadence so settings-changes are picked up
            // Slow-tick fallback. Use the larger of (configured, 60s).
            return Math.Max(pollInterval, 60);
        }

        if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            _logger.LogWarning(
                "Telavox poll tick skipped: previous tick still running. Raise {Setting} if this is persistent.",
                SettingKeys.Telavox.PollIntervalSeconds);
            return pollInterval;
        }

        try
        {
            await TickAsync(sp, ct);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _running, 0);
        }
        return pollInterval;
    }

    private async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var secrets = sp.GetRequiredService<IProtectedSecretStore>();
        var links = sp.GetRequiredService<ITelavoxAgentLinkStore>();
        var callState = sp.GetRequiredService<ITelavoxCallStateStore>();
        var apiClient = sp.GetRequiredService<ITelavoxApiClient>();
        var notifier = sp.GetRequiredService<ITelavoxCallNotifier>();
        var integrationAudit = sp.GetRequiredService<IIntegrationAuditLogger>();

        var allLinks = await links.ListAsync(ct);
        if (allLinks.Count == 0) return;

        var triggerMode = (await settings.GetAsync<string>(SettingKeys.Telavox.PopupTriggerMode, ct)
            ?? TelavoxCallTransition.Answered).Trim();

        var tickStart = Stopwatch.StartNew();
        var pollsAttempted = 0;
        var pollsSucceeded = 0;
        var pollsFailed = 0;
        var firesEmitted = 0;
        var firesFailed = 0;
        IntegrationAuditOutcome tickOutcome = IntegrationAuditOutcome.Ok;

        foreach (var link in allLinks)
        {
            ct.ThrowIfCancellationRequested();
            pollsAttempted++;
            var token = await secrets.GetAsync(ProtectedSecretKeys.TelavoxAgentCapiToken(link.UserId), ct);
            if (string.IsNullOrEmpty(token))
            {
                // The link row exists but the secret has been wiped (admin
                // manually cleared protected_secrets, DataProtection key
                // rotation that lost the row, …). Surface as a per-agent
                // error so the integration page flags it; don't fail the
                // whole tick.
                pollsFailed++;
                tickOutcome = IntegrationAuditOutcome.Warn;
                await links.UpdatePollOutcomeAsync(
                    link.UserId, DateTime.UtcNow, "missing_secret",
                    link.ConsecutiveErrors + 1, ct);
                continue;
            }

            try
            {
                var current = await apiClient.GetCurrentCallAsync(token, ct);
                var prior = await callState.GetAsync(link.UserId, ct);
                var decision = TelavoxCallTransition.Evaluate(
                    prior, current, triggerMode, link.UserId, DateTime.UtcNow);

                // Persist the new baseline first so a SignalR push failure
                // (rare, but possible) can't cause a duplicate fire on the
                // next tick from the same edge.
                await callState.UpsertAsync(
                    link.UserId,
                    decision.NewBaseline.LastCallId,
                    decision.NewBaseline.LastState,
                    decision.NewBaseline.LastSeenUtc,
                    ct);

                if (decision.ShouldFire && current is not null)
                {
                    var payload = new TelavoxCallEventPayload(
                        CallId: current.CallId,
                        State: current.State,
                        FromNumber: current.FromNumber,
                        ToNumber: current.ToNumber,
                        StartUtc: current.StartUtc,
                        OccurredUtc: DateTimeOffset.UtcNow);
                    try
                    {
                        await notifier.NotifyCallAsync(link.UserId, payload, ct);
                        firesEmitted++;
                    }
                    catch (Exception pushEx)
                    {
                        // SignalR push failed (e.g. backplane hiccup). The
                        // baseline was already upserted so the next tick
                        // sees same-call/same-state and debounces — the
                        // popup for THIS edge is permanently lost. Bump
                        // firesFailed + escalate tickOutcome so a backplane
                        // outage shows up in the per-tick integration-audit
                        // summary instead of being buried in a log line.
                        firesFailed++;
                        tickOutcome = IntegrationAuditOutcome.Warn;
                        _logger.LogWarning(pushEx,
                            "Telavox call push to user {UserId} failed; tick continues.",
                            link.UserId);
                    }
                }

                pollsSucceeded++;
                await links.UpdatePollOutcomeAsync(
                    link.UserId, DateTime.UtcNow, lastPollError: null,
                    consecutiveErrors: 0, ct);
            }
            catch (TelavoxApiException apiEx)
            {
                pollsFailed++;
                tickOutcome = IntegrationAuditOutcome.Warn;
                await links.UpdatePollOutcomeAsync(
                    link.UserId,
                    DateTime.UtcNow,
                    apiEx.UpstreamErrorCode ?? apiEx.HttpStatus?.ToString() ?? "api_error",
                    link.ConsecutiveErrors + 1,
                    ct);
                _logger.LogInformation(
                    "Telavox poll for user {UserId} failed: {Status} {Code}",
                    link.UserId, apiEx.HttpStatus, apiEx.UpstreamErrorCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                pollsFailed++;
                tickOutcome = IntegrationAuditOutcome.Warn;
                await links.UpdatePollOutcomeAsync(
                    link.UserId, DateTime.UtcNow, "tick_exception",
                    link.ConsecutiveErrors + 1, ct);
                _logger.LogWarning(ex,
                    "Telavox poll for user {UserId} threw an unexpected exception.",
                    link.UserId);
            }
        }
        tickStart.Stop();

        await integrationAudit.LogAsync(new IntegrationAuditEvent(
            Integration: TelavoxEventTypes.Integration,
            EventType: TelavoxEventTypes.AgentCallsPoll,
            Outcome: tickOutcome,
            LatencyMs: (int)tickStart.ElapsedMilliseconds,
            Payload: new
            {
                pollsAttempted,
                pollsSucceeded,
                pollsFailed,
                firesEmitted,
                firesFailed,
                triggerMode,
            }), ct);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* Invalid IANA id — fall through. */ }
        }
        return TimeZoneInfo.Local;
    }
}
