using System.Diagnostics;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Health;

/// Read-through cache in front of both health aggregators.
///
/// Why this exists (v0.0.99): every open browser tab polls the health
/// endpoints (dashboard pill on every page, critical banner, dashboard tiles,
/// Health page), and each uncached evaluation runs dozens of queries across
/// both aggregators. Because the tabs poll on the same cadence their requests
/// arrive in bursts, and each in-flight evaluation holds a pooled connection
/// — on a busy morning that alone pushed Postgres past its connection slots
/// (53300 "remaining connection slots are reserved for roles with the
/// SUPERUSER attribute"), which then surfaced as unrelated worker crashes.
///
/// Semantics:
///   • TTL comes from `Health.ReportCacheSeconds` (0 disables the cache).
///   • Single-flight: concurrent callers that miss the cache wait for the one
///     evaluation in progress instead of each starting their own.
///   • Mutating admin actions call <see cref="Invalidate"/> so the next poll
///     recomputes immediately (acknowledge / reset / requeue must reflect at
///     once — see the "cache invalidation after mutations" rule).
public interface IHealthReportCache
{
    Task<HealthReport> GetSystemReportAsync(CancellationToken ct);
    Task<IntegrationsHealthReport> GetIntegrationsReportAsync(CancellationToken ct);

    /// Drops both cached reports. Cheap; safe to call from any thread.
    void Invalidate();
}

public sealed class HealthReportCache : IHealthReportCache
{
    public const int TtlFloorSeconds = 0;
    public const int TtlCeilingSeconds = 300;
    private const int TtlFallbackSeconds = 10;

    private readonly IHealthAggregator _system;
    private readonly IIntegrationsHealthAggregator _integrations;
    private readonly ISettingsService _settings;

    private readonly Entry<HealthReport> _systemEntry = new();
    private readonly Entry<IntegrationsHealthReport> _integrationsEntry = new();

    public HealthReportCache(
        IHealthAggregator system,
        IIntegrationsHealthAggregator integrations,
        ISettingsService settings)
    {
        _system = system;
        _integrations = integrations;
        _settings = settings;
    }

    public async Task<HealthReport> GetSystemReportAsync(CancellationToken ct)
        => await _systemEntry.GetAsync(_system.CollectAsync, await GetTtlAsync(ct), ct);

    public async Task<IntegrationsHealthReport> GetIntegrationsReportAsync(CancellationToken ct)
        => await _integrationsEntry.GetAsync(_integrations.CollectAsync, await GetTtlAsync(ct), ct);

    public void Invalidate()
    {
        _systemEntry.Invalidate();
        _integrationsEntry.Invalidate();
    }

    private async Task<TimeSpan> GetTtlAsync(CancellationToken ct)
    {
        int seconds;
        try
        {
            seconds = await _settings.GetAsync<int>(SettingKeys.Health.ReportCacheSeconds, ct);
        }
        catch
        {
            seconds = TtlFallbackSeconds;
        }
        return TimeSpan.FromSeconds(Math.Clamp(seconds, TtlFloorSeconds, TtlCeilingSeconds));
    }

    /// One cached value with a monotonic timestamp and a single-flight gate.
    /// The gate is only taken on a miss, so cache hits stay lock-free.
    private sealed class Entry<T> where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private T? _value;
        private long _stamp;

        public async Task<T> GetAsync(Func<CancellationToken, Task<T>> compute, TimeSpan ttl, CancellationToken ct)
        {
            if (ttl <= TimeSpan.Zero)
                return await compute(ct);

            if (TryGetFresh(ttl, out var hit)) return hit;

            await _gate.WaitAsync(ct);
            try
            {
                // Somebody else may have finished the evaluation while we
                // were queued on the gate — re-check before recomputing.
                if (TryGetFresh(ttl, out hit)) return hit;

                var value = await compute(ct);
                Volatile.Write(ref _value, value);
                Volatile.Write(ref _stamp, Stopwatch.GetTimestamp());
                return value;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Invalidate() => Volatile.Write(ref _value, null);

        private bool TryGetFresh(TimeSpan ttl, out T value)
        {
            var v = Volatile.Read(ref _value);
            if (v is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref _stamp)) < ttl)
            {
                value = v;
                return true;
            }
            value = null!;
            return false;
        }
    }
}
