using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
public sealed class SlaRecalcWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SlaRecalcWorker> _logger;

    public SlaRecalcWorker(IServiceProvider sp, ILogger<SlaRecalcWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        SlaRecalcCursor? cursor = null;
        var catchUpDone = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                interval = Math.Max(15, await settings.GetAsync<int>(SettingKeys.Sla.RecalcIntervalSeconds, stoppingToken));
                var batch = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Sla.RecalcBatchSize, stoppingToken), 10, 5000);

                var repo = scope.ServiceProvider.GetRequiredService<ISlaRepository>();
                var engine = scope.ServiceProvider.GetRequiredService<ISlaEngine>();

                if (!catchUpDone)
                {
                    // One-off: resolved-but-open tickets without a state row.
                    // Loops until the anti-join comes back empty (every recalc
                    // creates the row, so this converges), bounded per cycle.
                    var caught = 0;
                    IReadOnlyList<Guid> missing;
                    do
                    {
                        missing = await repo.ListResolvedWithoutStateAsync(batch, stoppingToken);
                        foreach (var id in missing)
                        {
                            if (stoppingToken.IsCancellationRequested) break;
                            await engine.RecalcAsync(id, stoppingToken);
                        }
                        caught += missing.Count;
                    } while (missing.Count == batch && caught < batch * 10 && !stoppingToken.IsCancellationRequested);
                    catchUpDone = missing.Count < batch;
                    if (caught > 0)
                        _logger.LogInformation("SLA recalc: created state rows for {Count} resolved tickets without one.", caught);
                }

                var candidates = await repo.ListRecalcCandidatesAsync(batch, cursor, stoppingToken);
                foreach (var c in candidates)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await engine.RecalcAsync(c.Id, stoppingToken);
                }
                // Advance the cursor; a short batch means the pass is complete —
                // start over from the least-recently-updated ticket next cycle.
                cursor = candidates.Count < batch
                    ? null
                    : new SlaRecalcCursor(candidates[^1].UpdatedUtc, candidates[^1].Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SLA recalc worker iteration failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
