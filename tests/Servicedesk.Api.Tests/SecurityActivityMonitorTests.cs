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
