using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.99 — the health report cache is what keeps N polling tabs from
/// turning into N × (dozens of queries) bursts against Postgres. These pin
/// its three guarantees: single-flight under concurrency, TTL reuse, and
/// immediate invalidation after admin actions.
public sealed class HealthReportCacheTests
{
    [Fact]
    public async Task Concurrent_misses_share_one_evaluation()
    {
        var system = new CountingSystemAggregator { Delay = TimeSpan.FromMilliseconds(150) };
        var cache = Build(system, ttlSeconds: 10);

        var tasks = Enumerable.Range(0, 25).Select(_ => cache.GetSystemReportAsync(default)).ToArray();
        var reports = await Task.WhenAll(tasks);

        Assert.Equal(1, system.Calls);
        Assert.All(reports, r => Assert.Same(reports[0], r));
    }

    [Fact]
    public async Task Fresh_value_is_reused_within_ttl()
    {
        var system = new CountingSystemAggregator();
        var cache = Build(system, ttlSeconds: 10);

        await cache.GetSystemReportAsync(default);
        await cache.GetSystemReportAsync(default);
        await cache.GetIntegrationsReportAsync(default);

        Assert.Equal(1, system.Calls);
    }

    [Fact]
    public async Task Invalidate_forces_recompute_on_next_read()
    {
        var system = new CountingSystemAggregator();
        var integrations = new CountingIntegrationsAggregator();
        var cache = Build(system, integrations, ttlSeconds: 10);

        await cache.GetSystemReportAsync(default);
        await cache.GetIntegrationsReportAsync(default);
        cache.Invalidate();
        await cache.GetSystemReportAsync(default);
        await cache.GetIntegrationsReportAsync(default);

        Assert.Equal(2, system.Calls);
        Assert.Equal(2, integrations.Calls);
    }

    [Fact]
    public async Task Ttl_zero_bypasses_the_cache()
    {
        var system = new CountingSystemAggregator();
        var cache = Build(system, ttlSeconds: 0);

        await cache.GetSystemReportAsync(default);
        await cache.GetSystemReportAsync(default);

        Assert.Equal(2, system.Calls);
    }

    [Fact]
    public async Task Cached_report_expires_after_ttl()
    {
        var system = new CountingSystemAggregator();
        var cache = Build(system, ttlSeconds: 1);

        await cache.GetSystemReportAsync(default);
        await Task.Delay(TimeSpan.FromMilliseconds(1300));
        await cache.GetSystemReportAsync(default);

        Assert.Equal(2, system.Calls);
    }

    private static HealthReportCache Build(CountingSystemAggregator system, int ttlSeconds) =>
        Build(system, new CountingIntegrationsAggregator(), ttlSeconds);

    private static HealthReportCache Build(
        CountingSystemAggregator system, CountingIntegrationsAggregator integrations, int ttlSeconds)
    {
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Health.ReportCacheSeconds, ttlSeconds.ToString());
        return new HealthReportCache(system, integrations, settings);
    }

    private sealed class CountingSystemAggregator : IHealthAggregator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public async Task<HealthReport> CollectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new HealthReport(HealthStatus.Ok, Array.Empty<SubsystemHealth>());
        }
    }

    private sealed class CountingIntegrationsAggregator : IIntegrationsHealthAggregator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<IntegrationsHealthReport> CollectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new IntegrationsHealthReport(HealthStatus.Ok, Array.Empty<IntegrationHealth>()));
        }
    }
}
