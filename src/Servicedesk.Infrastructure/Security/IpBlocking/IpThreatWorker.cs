using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

/// Two loops:
/// - **Observations** (event-driven): every IP whose threshold the detector
///   saw crossed is turned into a proposal right away; unambiguous scanners
///   are temp-blocked within milliseconds, not on the next poll.
/// - **Housekeeping** (every minute): refresh detector config from settings,
///   reload the rule cache from the DB (also picks up console break-glass
///   edits), drop expired auto-blocks, flush blocked-hit counters, sweep idle
///   detector state.
public sealed class IpThreatWorker : BackgroundService
{
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IIpThreatDetector _detector;
    private readonly IIpBlockList _list;
    private readonly ILogger<IpThreatWorker> _logger;
    private readonly TimeProvider _clock;

    public IpThreatWorker(IServiceScopeFactory scopes, IIpThreatDetector detector, IIpBlockList list,
        ILogger<IpThreatWorker> logger, TimeProvider? clock = null)
    {
        _scopes = scopes;
        _detector = detector;
        _list = list;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let DatabaseBootstrapper + settings seeding finish first.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await HousekeepingOnceAsync(stoppingToken);

        var observations = ProcessObservationsAsync(stoppingToken);
        var housekeeping = HousekeepingLoopAsync(stoppingToken);
        await Task.WhenAll(observations, housekeeping);
    }

    private async Task ProcessObservationsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var observation in _detector.Tripped.ReadAllAsync(ct))
            {
                using var run = PerfWorkerRun.Start("ip-threat");
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<IIpBlockService>();
                    var (newProposalId, autoBlocked) = await service.HandleObservationAsync(observation, ct);
                    run.AddItems(1);
                    if (newProposalId is not null)
                    {
                        var notifier = scope.ServiceProvider.GetService<ISecurityAlertNotifier>();
                        if (notifier is not null)
                        {
                            await notifier.NotifyAdminsAsync(new SecurityAlertPush(
                                Severity: "Warning",
                                Subsystem: "ip-blocking",
                                Summary: autoBlocked
                                    ? $"{observation.Ip} probed scanner paths and was blocked temporarily — confirm or release it."
                                    : $"{observation.Ip} looks suspicious ({string.Join(", ", observation.TrippedKinds.Select(IpBlockService.ReasonKey))}) — review the block proposal.",
                                IncidentId: null,
                                CreatedUtc: _clock.GetUtcNow().UtcDateTime), ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    run.Fail(ex);
                    _logger.LogWarning(ex, "IP threat observation for {Ip} failed.", observation.Ip);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HousekeepingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HousekeepingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await HousekeepingOnceAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HousekeepingOnceAsync(CancellationToken ct)
    {
        using var run = PerfWorkerRun.Start("ip-threat-housekeeping");
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var repo = scope.ServiceProvider.GetRequiredService<IIpBlockRepository>();

            _detector.UpdateConfig(await LoadConfigAsync(settings, ct));

            await repo.DeleteExpiredBlocksAsync(ct);
            await repo.AddBlockedHitsAsync(_list.DrainBlockedHits(), ct);
            _list.Replace(await repo.ListActiveRulesAsync(ct));
            _list.SetOpenProposalCount(await repo.CountOpenProposalsAsync(ct));
            _detector.Sweep(_clock.GetUtcNow().UtcDateTime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            // Keep the last known rules in memory — a DB hiccup must not
            // silently lift every block.
            _logger.LogWarning(ex, "IP blocking housekeeping failed; keeping the cached rules.");
        }
    }

    internal static async Task<IpThreatConfig> LoadConfigAsync(ISettingsService s, CancellationToken ct)
    {
        var enabled = await s.GetAsync<bool>(SettingKeys.Security.IpBlockingEnabled, ct);
        return new IpThreatConfig(
            Enabled: enabled,
            Window: TimeSpan.FromMinutes(Math.Clamp(await s.GetAsync<int>(SettingKeys.Security.IpBlockingWindowMinutes, ct), 1, 1440)),
            ScannerThreshold: Math.Max(1, await s.GetAsync<int>(SettingKeys.Security.IpBlockingScannerThreshold, ct)),
            RateLimitThreshold: Math.Max(1, await s.GetAsync<int>(SettingKeys.Security.IpBlockingRateLimitThreshold, ct)),
            CsrfThreshold: Math.Max(1, await s.GetAsync<int>(SettingKeys.Security.IpBlockingCsrfThreshold, ct)),
            FailedLoginThreshold: Math.Max(1, await s.GetAsync<int>(SettingKeys.Security.IpBlockingFailedLoginThreshold, ct)),
            AutoBlockDuration: TimeSpan.FromHours(Math.Clamp(await s.GetAsync<int>(SettingKeys.Security.IpBlockingAutoBlockHours, ct), 1, 24 * 30)),
            ScannerPathPattern: enabled
                ? CompileCached(await s.GetAsync<string>(SettingKeys.Security.IpBlockingScannerPaths, ct))
                : null);
    }

    private static (string? Source, System.Text.RegularExpressions.Regex? Regex) _patternCache;

    // Recompile only when the setting text actually changed (compiled regexes
    // are not free; this runs every minute).
    private static System.Text.RegularExpressions.Regex? CompileCached(string? patterns)
    {
        var cached = _patternCache;
        if (cached.Source == patterns) return cached.Regex;
        var rx = IpThreatConfig.CompileScannerPatterns(patterns);
        _patternCache = (patterns, rx);
        return rx;
    }
}
