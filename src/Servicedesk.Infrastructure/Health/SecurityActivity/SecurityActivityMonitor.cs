using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Health.SecurityActivity;

/// Background loop that samples the <c>audit_log</c> on a rolling window,
/// evaluates per-category thresholds via <see cref="SecurityActivityEvaluator"/>,
/// stores the result in <see cref="ISecurityActivitySnapshot"/> for the
/// HealthAggregator to render, and fires an incident + admin push on
/// upward severity transitions. Runs independently of
/// <c>/api/admin/health</c> polls so alerts fire even when no admin is
/// currently online.
public sealed class SecurityActivityMonitor : BackgroundService
{
    // Defensive floor so a misconfigured interval (0 or negative) doesn't
    // turn the background service into a busy-loop against Postgres.
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecurityActivitySnapshot _snapshot;
    private readonly ILogger<SecurityActivityMonitor> _logger;
    private readonly TimeProvider _clock;

    public SecurityActivityMonitor(
        IServiceScopeFactory scopes,
        ISecurityActivitySnapshot snapshot,
        ILogger<SecurityActivityMonitor> logger,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _snapshot = snapshot;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay the first tick a bit so DatabaseBootstrapper + SettingsSeeder
        // have finished migrating schema / seeding defaults. Without this the
        // first evaluation can race against a pending `CREATE TABLE audit_log`
        // on a fresh install.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval;
            try
            {
                interval = await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Swallow — this loop MUST NOT crash the host. Log + keep
                // going so a transient Postgres hiccup doesn't disable
                // monitoring forever.
                _logger.LogWarning(ex, "Security-activity monitor tick failed; will retry.");
                interval = TimeSpan.FromSeconds(30);
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// Executes one evaluation pass. Returns the delay until the next pass.
    /// Extracted so tests can drive a single tick without scheduling.
    public async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditQuery>();
        var incidents = scope.ServiceProvider.GetRequiredService<IIncidentLog>();
        var notifier = scope.ServiceProvider.GetRequiredService<ISecurityAlertNotifier>();

        var enabled = await settings.GetAsync<bool>(SettingKeys.Health.SecurityActivityEnabled, ct);
        var windowSec = await settings.GetAsync<int>(SettingKeys.Health.SecurityActivityWindowSeconds, ct);
        var intervalSec = await settings.GetAsync<int>(SettingKeys.Health.SecurityActivityIntervalSeconds, ct);
        var multiplier = await settings.GetAsync<int>(SettingKeys.Health.SecurityActivityCriticalMultiplier, ct);

        var window = TimeSpan.FromSeconds(Math.Max(windowSec, (int)MinWindow.TotalSeconds));
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSec, (int)MinInterval.TotalSeconds));

        var thresholds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cat in SecurityActivityCategories.All)
        {
            thresholds[cat.Key] = await settings.GetAsync<int>(cat.ThresholdSettingKey, ct);
        }

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var windowStart = nowUtc - window;

        // Apply the ack-baseline: after an admin acknowledges an incident,
        // we only count events that occurred after the ack moment. Once
        // the rolling window has rolled completely past the ack, the
        // baseline is discarded and normal full-window counting resumes.
        var ackFrom = _snapshot.GetAcknowledgedFromUtc();
        if (ackFrom is { } ack && ack <= windowStart)
        {
            _snapshot.SetAcknowledgedFromUtc(null);
            ackFrom = null;
        }

        var effectiveFromUtc = ackFrom is { } a && a > windowStart ? a : windowStart;

        IReadOnlyDictionary<string, int> counts;
        if (!enabled)
        {
            counts = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        else
        {
            var fromUtc = new DateTimeOffset(effectiveFromUtc, TimeSpan.Zero);
            var toUtc = new DateTimeOffset(nowUtc, TimeSpan.Zero);
            counts = await audit.CountByEventTypesAsync(
                SecurityActivityCategories.AllEventTypes, fromUtc, toUtc, ct);
        }

        var snapshot = SecurityActivityEvaluator.Evaluate(
            countsByEventType: counts,
            thresholdsByCategoryKey: thresholds,
            criticalMultiplier: multiplier,
            window: window,
            nowUtc: nowUtc,
            monitorEnabled: enabled,
            acknowledgedFromUtc: ackFrom);

        _snapshot.Set(snapshot);

        // Reset the alert-guard when monitoring is off, or when no category
        // is tripping — the next upward transition should always fire.
        if (!enabled || snapshot.Status == HealthStatus.Ok)
        {
            _snapshot.SetLastAlertedSeverity(HealthStatus.Ok);
            return interval;
        }

        var lastAlerted = _snapshot.GetLastAlertedSeverity();
        if (snapshot.Status > lastAlerted)
        {
            await RaiseAlertAsync(snapshot, incidents, notifier, ct);
            _snapshot.SetLastAlertedSeverity(snapshot.Status);
        }

        return interval;
    }

    private static async Task RaiseAlertAsync(
        SecurityActivitySnapshot snapshot,
        IIncidentLog incidents,
        ISecurityAlertNotifier notifier,
        CancellationToken ct)
    {
        var severity = snapshot.Status == HealthStatus.Critical
            ? IncidentSeverity.Critical
            : IncidentSeverity.Warning;

        // Keep the message stable per severity so the 60s dedup in
        // IncidentLog bumps occurrence_count instead of inserting a new row
        // per tick; per-category counts go into the JSON context for admins
        // to drill into.
        var message = severity == IncidentSeverity.Critical
            ? "Severe spike in security activity — thresholds exceeded by the critical multiplier."
            : "Elevated security activity — one or more thresholds exceeded.";

        var hotCategories = snapshot.Categories
            .Where(c => c.Status != HealthStatus.Ok)
            .Select(c => new
            {
                key = c.Key,
                label = c.Label,
                count = c.Count,
                threshold = c.Threshold,
                criticalThreshold = c.CriticalThreshold,
                status = c.Status.ToString(),
            })
            .ToArray();

        var contextJson = JsonSerializer.Serialize(new
        {
            evaluatedUtc = snapshot.EvaluatedUtc,
            windowSeconds = (int)snapshot.Window.TotalSeconds,
            summary = snapshot.Summary,
            categories = hotCategories,
        });

        await incidents.ReportAsync(
            subsystem: "security-activity",
            severity: severity,
            message: message,
            details: snapshot.Summary,
            contextJson: contextJson,
            ct: ct);

        var push = new SecurityAlertPush(
            Severity: severity.ToString(),
            Subsystem: "security-activity",
            Summary: snapshot.Summary,
            IncidentId: null,
            CreatedUtc: snapshot.EvaluatedUtc);

        await notifier.NotifyAdminsAsync(push, ct);
    }
}
