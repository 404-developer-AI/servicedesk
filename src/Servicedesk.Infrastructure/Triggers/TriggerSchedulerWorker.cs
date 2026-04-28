using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers.Actions;

namespace Servicedesk.Infrastructure.Triggers;

/// v0.0.24 Blok 5 — fires time-based triggers when a temporal boundary
/// elapses. Three boundaries are watched per tick:
/// <list type="bullet">
/// <item><c>reminder</c>: <c>tickets.pending_till_utc</c> reaches now()</item>
/// <item><c>escalation</c>: an SLA first-response or resolution deadline elapses</item>
/// <item><c>escalation_warning</c>: that same deadline minus the configured warning offset elapses while the deadline itself is still in the future</item>
/// </list>
/// Dedup is at the SQL layer: each candidate query returns only
/// (ticket, trigger) pairs that have not yet produced an applied/failed
/// run row past the relevant boundary. Skipped_no_match runs do not
/// dedup — a trigger whose conditions weren't met yet should re-evaluate
/// when the ticket changes shape.
public sealed class TriggerSchedulerWorker : BackgroundService
{
    private const int CandidateLimit = 500;

    private readonly IServiceProvider _sp;
    private readonly ILogger<TriggerSchedulerWorker> _logger;

    /// Single-instance overlap guard. Today the loop below already runs
    /// ticks sequentially so this is belt-and-braces, but if a future
    /// refactor fans the loop out (e.g. <c>Task.Run</c>) or a hosted-
    /// service refresh ever spawns a second instance into the same
    /// process, this guarantees only one tick scans the candidate
    /// tables at a time. <c>0 = idle, 1 = running</c>; CompareExchange
    /// races safely.
    private int _running;

    public TriggerSchedulerWorker(IServiceProvider sp, ILogger<TriggerSchedulerWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first tick so the scheduler doesn't race the SLA
        // recalc worker on container start. SlaRecalcWorker waits 10s; we
        // wait 20s so the SLA state table is populated by the time we
        // scan it for escalation candidates.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 60;
            try
            {
                using var scope = _sp.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                interval = Math.Max(15, await settings.GetAsync<int>(
                    SettingKeys.Triggers.SchedulerIntervalSeconds, stoppingToken));

                if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    _logger.LogWarning(
                        "Trigger scheduler tick skipped: previous tick still running. Increase {Setting} if this happens consistently.",
                        SettingKeys.Triggers.SchedulerIntervalSeconds);
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
                _logger.LogError(ex, "Trigger scheduler tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<ITriggerRepository>();
        var triggerService = sp.GetRequiredService<ITriggerService>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var mutator = sp.GetRequiredService<SystemFieldMutator>();

        // Reminder candidates: pending-till elapsed. The repository
        // returns chained candidates (`IsChainedReminder = true`) ahead
        // of wide-scan ones for ergonomics. Chained-pointer state is
        // cleared only on Applied/Failed — a SkippedNoMatch keeps the
        // pointer + pending_till in place so the next tick can re-
        // evaluate against possibly-changed ticket state. The clear
        // method also wipes pending_till_utc (under an optimistic guard
        // so a chained re-arm via set_pending_till is preserved),
        // which prevents the wide branch from picking the ticket back
        // up after the chain is consumed.
        var reminders = await repo.ListReminderCandidatesAsync(CandidateLimit, ct);
        foreach (var c in reminders)
        {
            if (ct.IsCancellationRequested) return;
            var result = await triggerService.EvaluateScheduledAsync(
                c.TriggerId, c.TicketId, c.BoundaryUtc, "reminder", ct);
            if (c.IsChainedReminder && result.ChainShouldClear)
            {
                await mutator.ClearChainedReminderStateAsync(
                    c.TicketId, c.TriggerId, c.BoundaryUtc, ct);
            }
        }

        // Escalation candidates: SLA deadline elapsed.
        var escalations = await repo.ListEscalationCandidatesAsync(CandidateLimit, ct);
        foreach (var c in escalations)
        {
            if (ct.IsCancellationRequested) return;
            await triggerService.EvaluateScheduledAsync(
                c.TriggerId, c.TicketId, c.BoundaryUtc, "escalation", ct);
        }

        // Warning candidates: deadline minus offset elapsed, deadline still ahead.
        var warningMinutes = Math.Max(1, await settings.GetAsync<int>(
            SettingKeys.Triggers.EscalationWarningMinutes, ct));
        var warnings = await repo.ListEscalationWarningCandidatesAsync(
            warningMinutes, CandidateLimit, ct);
        foreach (var c in warnings)
        {
            if (ct.IsCancellationRequested) return;
            await triggerService.EvaluateScheduledAsync(
                c.TriggerId, c.TicketId, c.BoundaryUtc, "escalation_warning", ct);
        }
    }
}
