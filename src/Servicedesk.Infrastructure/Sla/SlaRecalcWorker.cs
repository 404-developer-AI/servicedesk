using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Hosting;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Sla;

/// Periodically re-runs the SLA engine for open tickets so business-minutes
/// consumed, paused-state, and breach flags stay fresh even when no ticket
/// event fires. Cadence is driven by Sla.RecalcIntervalSeconds, batch size by
/// Sla.RecalcBatchSize.
///
/// v0.0.101: the sweep walks the whole open set with a keyset cursor across
/// cycles (one batch per cycle, cursor resets when a batch comes back short)
/// instead of recomputing the same oldest-updated N tickets every cycle and
/// never reaching the rest. Per ticket the engine now does one batched read
/// + change-only writes with policies/schemas served from an in-process
/// snapshot, so a cycle is cheap enough to run every minute. Resolved-but-open
/// tickets are excluded from the sweep (their SLA numbers are frozen until
/// reopened, which recalcs on its own); the ones that never got a state row
/// are picked up once at start-up.
public sealed class SlaRecalcWorker : PeriodicWorker
{
    private SlaRecalcCursor? _cursor;
    private bool _catchUpDone;

    public SlaRecalcWorker(IServiceProvider sp, ILogger<SlaRecalcWorker> logger) : base(sp, logger) { }

    protected override string WorkerName => "sla-recalc";
    protected override TimeSpan StartDelay => TimeSpan.FromSeconds(10);
    protected override TimeSpan FallbackInterval => TimeSpan.FromSeconds(60);

    protected override async Task<WorkerSchedule> GetScheduleAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var seconds = Math.Max(15, await settings.GetAsync<int>(SettingKeys.Sla.RecalcIntervalSeconds, ct));
        return new WorkerSchedule(TimeSpan.FromSeconds(seconds));
    }

    protected override async Task RunAsync(IServiceProvider services, PerfWorkerRun run, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var batch = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Sla.RecalcBatchSize, ct), 10, 5000);

        var repo = services.GetRequiredService<ISlaRepository>();
        var engine = services.GetRequiredService<ISlaEngine>();

        // v0.1.25 — Sla.Enabled off: idle. Reset the cursor and
        // the catch-up so switching it back on starts a fresh
        // full pass over the open tickets.
        if (!await engine.IsEnabledAsync(ct))
        {
            _cursor = null;
            _catchUpDone = false;
            return;
        }

        if (!_catchUpDone)
        {
            // One-off: resolved-but-open tickets without a state row.
            // Loops until the anti-join comes back empty (every recalc
            // creates the row, so this converges), bounded per cycle.
            var caught = 0;
            IReadOnlyList<Guid> missing;
            do
            {
                missing = await repo.ListResolvedWithoutStateAsync(batch, ct);
                await engine.RecalcManyAsync(missing, ct);
                caught += missing.Count;
            } while (missing.Count == batch && caught < batch * 10 && !ct.IsCancellationRequested);
            _catchUpDone = missing.Count < batch;
            if (caught > 0)
                Logger.LogInformation("SLA recalc: created state rows for {Count} resolved tickets without one.", caught);
        }

        var candidates = await repo.ListRecalcCandidatesAsync(batch, _cursor, ct);
        run.AddItems(candidates.Count);
        // v0.1.25 — one bulk read + one bulk write per batch
        // instead of a few round-trips per ticket.
        await engine.RecalcManyAsync(candidates.Select(c => c.Id).ToList(), ct);
        // Advance the cursor; a short batch means the pass is complete —
        // start over from the least-recently-updated ticket next cycle.
        _cursor = candidates.Count < batch
            ? null
            : new SlaRecalcCursor(candidates[^1].UpdatedUtc, candidates[^1].Id);
    }
}
