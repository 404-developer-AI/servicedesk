using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Health.SecurityActivity;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SecurityActivityMonitorTests
{
    [Fact]
    public async Task Calm_activity_updates_snapshot_to_ok_without_alerting()
    {
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal));
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None);

        var snap = snapshot.Get();
        Assert.NotNull(snap);
        Assert.Equal(HealthStatus.Ok, snap!.Status);
        Assert.Empty(incidents.Reports);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task First_crossing_fires_incident_and_admin_push()
    {
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12, // default threshold 10 → Warning
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None);

        var snap = snapshot.Get();
        Assert.Equal(HealthStatus.Warning, snap!.Status);
        var report = Assert.Single(incidents.Reports);
        Assert.Equal("security-activity", report.Subsystem);
        Assert.Equal(IncidentSeverity.Warning, report.Severity);
        var push = Assert.Single(notifier.Sent);
        Assert.Equal("Warning", push.Severity);
        Assert.Equal("security-activity", push.Subsystem);
    }

    [Fact]
    public async Task Sustained_warning_does_not_refire_on_next_tick()
    {
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12,
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, _) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None);
        await monitor.TickAsync(CancellationToken.None);
        await monitor.TickAsync(CancellationToken.None);

        // Only the first crossing fires; the next ticks see the same severity
        // and are suppressed until it escalates or drops to Ok.
        Assert.Single(incidents.Reports);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Warning_escalating_to_critical_fires_a_second_alert()
    {
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12, // Warning
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, _) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None);

        // Attack intensifies — now above critical (10 × 3 = 30).
        audit.Counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 45,
        };
        await monitor.TickAsync(CancellationToken.None);

        Assert.Equal(2, incidents.Reports.Count);
        Assert.Equal(IncidentSeverity.Warning, incidents.Reports[0].Severity);
        Assert.Equal(IncidentSeverity.Critical, incidents.Reports[1].Severity);
        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal("Critical", notifier.Sent[1].Severity);
    }

    [Fact]
    public async Task Dropping_back_to_ok_resets_alert_guard_so_new_attack_refires()
    {
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12,
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, _) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None); // fires first warning
        audit.Counts = new Dictionary<string, int>(StringComparer.Ordinal); // calms down
        await monitor.TickAsync(CancellationToken.None); // no alert, guard resets
        audit.Counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 12 };
        await monitor.TickAsync(CancellationToken.None); // fires again

        Assert.Equal(2, incidents.Reports.Count);
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task Disabled_monitor_does_not_query_audit_log_and_keeps_snapshot_ok()
    {
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 999,
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier, enabled: false);

        await monitor.TickAsync(CancellationToken.None);

        Assert.Equal(0, audit.QueryCount);
        Assert.Equal(HealthStatus.Ok, snapshot.Get()!.Status);
        Assert.Empty(incidents.Reports);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Snapshot_clear_resets_alert_guard_so_ongoing_attack_refires()
    {
        // Simulates admin acknowledging the subsystem — HealthSubsystemReset
        // calls snapshot.Clear(), and the next tick should re-raise the
        // incident so the admin is paged again if the attack continues.
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12,
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None); // first warning
        snapshot.Clear();
        await monitor.TickAsync(CancellationToken.None); // fires second warning

        Assert.Equal(2, incidents.Reports.Count);
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task Ack_narrows_query_window_to_events_after_ack()
    {
        // Admin acks at T. StubAuditQuery records the from/to bounds on
        // every call. The next tick after an ack must ask for events from
        // the ack moment onward, not from (now - windowSeconds).
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal));
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        var ackAt = DateTime.UtcNow.AddSeconds(-5);
        snapshot.Acknowledge(ackAt);

        await monitor.TickAsync(CancellationToken.None);

        var lastFrom = audit.LastFromUtc;
        Assert.True(lastFrom >= new DateTimeOffset(ackAt, TimeSpan.Zero).AddMilliseconds(-1),
            $"Expected fromUtc >= ack ({ackAt:o}) but got {lastFrom:o}");
    }

    [Fact]
    public async Task Ack_suppresses_refire_when_only_pre_ack_events_exist()
    {
        // The audit store has 12 login_failed events that all predate the
        // ack. After ack, the monitor counts only post-ack events (zero)
        // → rollup drops to Ok → no new incident, no new push.
        // This is the bug the user reported: acknowledge clicked, but card
        // immediately flipped red again because the query window still
        // contained the ack'd events.
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12,
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None); // initial Warning
        Assert.Single(incidents.Reports);

        // Admin acks — from now on, the audit stub returns the same 12
        // events but the monitor should treat them as pre-ack and ignore
        // them. (The stub doesn't filter by time itself; the monitor's
        // window narrowing is what matters here. To make the assertion
        // meaningful we switch the stub to "no events after ack".)
        snapshot.Acknowledge(DateTime.UtcNow);
        audit.Counts = new Dictionary<string, int>(StringComparer.Ordinal);

        await monitor.TickAsync(CancellationToken.None);

        // Still one report — no refire even though pre-ack events remain
        // (visible on the full rolling window).
        Assert.Single(incidents.Reports);
        Assert.Single(notifier.Sent);
        var snap = snapshot.Get();
        Assert.Equal(HealthStatus.Ok, snap!.Status);
        Assert.NotNull(snap.AcknowledgedFromUtc);
    }

    [Fact]
    public async Task Post_ack_new_attack_still_fires_fresh_incident()
    {
        // A persistent attack should still be able to re-page the admin.
        // Sequence: ack at T, then 12 NEW login_failed events arrive, next
        // tick should flip to Warning and fire a fresh incident + push.
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["login_failed"] = 12,
        });
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        await monitor.TickAsync(CancellationToken.None); // initial Warning
        snapshot.Acknowledge(DateTime.UtcNow);
        // 12 new events arrive post-ack.
        audit.Counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["login_failed"] = 12 };

        await monitor.TickAsync(CancellationToken.None);

        Assert.Equal(2, incidents.Reports.Count);
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task Ack_baseline_is_discarded_once_window_rolls_past_it()
    {
        // If the ack was more than one full window ago, the baseline is
        // effectively stale — all ack'd events have naturally aged out of
        // the rolling window. Monitor drops the baseline and reverts to
        // full-window querying.
        var audit = new StubAuditQuery(new Dictionary<string, int>(StringComparer.Ordinal));
        var incidents = new StubIncidentLog();
        var notifier = new StubAlertNotifier();
        var (monitor, snapshot) = BuildMonitor(audit, incidents, notifier);

        // Default window is 3600s → set ack to 2 hours ago.
        snapshot.Acknowledge(DateTime.UtcNow.AddHours(-2));

        await monitor.TickAsync(CancellationToken.None);

        Assert.Null(snapshot.GetAcknowledgedFromUtc());
    }

    private static (SecurityActivityMonitor monitor, ISecurityActivitySnapshot snapshot) BuildMonitor(
        StubAuditQuery audit,
        StubIncidentLog incidents,
        StubAlertNotifier notifier,
        bool enabled = true)
    {
        var snapshot = new InMemorySecurityActivitySnapshot();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(new StubHealthSettings(enabled));
        services.AddSingleton<IAuditQuery>(audit);
        services.AddSingleton<IIncidentLog>(incidents);
        services.AddSingleton<ISecurityAlertNotifier>(notifier);
        var sp = services.BuildServiceProvider();
        var monitor = new SecurityActivityMonitor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            snapshot,
            NullLogger<SecurityActivityMonitor>.Instance,
            TimeProvider.System);
        return (monitor, snapshot);
    }

    private sealed class StubAuditQuery : IAuditQuery
    {
        public IReadOnlyDictionary<string, int> Counts { get; set; }
        public int QueryCount { get; private set; }
        public DateTimeOffset LastFromUtc { get; private set; }
        public DateTimeOffset LastToUtc { get; private set; }
        public StubAuditQuery(IReadOnlyDictionary<string, int> counts) { Counts = counts; }

        public Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<AuditLogEntry?> GetAsync(long id, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<AuditPage> ListForContactAsync(Guid contactId, long? cursorId, int limit, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyDictionary<string, int>> CountByEventTypesAsync(
            IReadOnlyCollection<string> eventTypes, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            QueryCount++;
            LastFromUtc = fromUtc;
            LastToUtc = toUtc;
            return Task.FromResult(Counts);
        }
    }

    private sealed class StubIncidentLog : IIncidentLog
    {
        public List<IncidentReport> Reports { get; } = new();

        public Task ReportAsync(string subsystem, IncidentSeverity severity, string message, string? details, string? contextJson, CancellationToken ct)
        {
            Reports.Add(new IncidentReport(subsystem, severity, message, details, contextJson));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<IncidentRow>> ListOpenAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IncidentRow>>(Array.Empty<IncidentRow>());
        public Task<IReadOnlyList<IncidentRow>> ListOpenRecentAsync(int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IncidentRow>>(Array.Empty<IncidentRow>());
        public Task<IReadOnlyList<IncidentRow>> ListArchiveAsync(string? subsystem, int take, int skip, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IncidentRow>>(Array.Empty<IncidentRow>());
        public Task<string?> AcknowledgeAsync(long id, Guid userId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<int> AcknowledgeSubsystemAsync(string subsystem, Guid userId, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyDictionary<string, IncidentSeverity>> GetOpenBySubsystemAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, IncidentSeverity>>(new Dictionary<string, IncidentSeverity>());
    }

    private sealed record IncidentReport(
        string Subsystem,
        IncidentSeverity Severity,
        string Message,
        string? Details,
        string? ContextJson);

    private sealed class StubAlertNotifier : ISecurityAlertNotifier
    {
        public List<SecurityAlertPush> Sent { get; } = new();
        public Task NotifyAdminsAsync(SecurityAlertPush payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHealthSettings : ISettingsService
    {
        private readonly bool _enabled;
        public StubHealthSettings(bool enabled) { _enabled = enabled; }

        public Task<T> GetAsync<T>(string key, CancellationToken ct = default)
        {
            object value = key switch
            {
                SettingKeys.Health.SecurityActivityEnabled => _enabled,
                SettingKeys.Health.SecurityActivityWindowSeconds => 3600,
                SettingKeys.Health.SecurityActivityIntervalSeconds => 60,
                SettingKeys.Health.SecurityActivityCriticalMultiplier => 3,
                SettingKeys.Health.SecurityActivityThresholdLoginFailed => 10,
                SettingKeys.Health.SecurityActivityThresholdLoginLockedOut => 3,
                SettingKeys.Health.SecurityActivityThresholdCsrfRejected => 5,
                SettingKeys.Health.SecurityActivityThresholdRateLimited => 50,
                SettingKeys.Health.SecurityActivityThresholdMicrosoftLoginRejected => 5,
                _ => default(T)!,
            };
            return Task.FromResult((T)value!);
        }
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
