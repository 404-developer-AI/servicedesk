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

                await TickAsync(scope.ServiceProvider, stoppingToken);
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
        // of wide-scan ones for ergonomics — chain semantics: the
        // pointer is cleared after dispatch (any outcome) so the next
        // pending-cycle re-arms explicitly. Without the clear, a chained
        // trigger with no actions (or one that didn't reset pending_till)
        // would re-fire on every tick.
        var reminders = await repo.ListReminderCandidatesAsync(CandidateLimit, ct);
        foreach (var c in reminders)
        {
            if (ct.IsCancellationRequested) return;
            await triggerService.EvaluateScheduledAsync(c.TriggerId, c.TicketId, c.BoundaryUtc, ct);
            if (c.IsChainedReminder)
                await mutator.ClearPendingTillNextTriggerAsync(c.TicketId, ct);
        }

        // Escalation candidates: SLA deadline elapsed.
        var escalations = await repo.ListEscalationCandidatesAsync(CandidateLimit, ct);
        foreach (var c in escalations)
        {
            if (ct.IsCancellationRequested) return;
            await triggerService.EvaluateScheduledAsync(c.TriggerId, c.TicketId, c.BoundaryUtc, ct);
        }

        // Warning candidates: deadline minus offset elapsed, deadline still ahead.
        var warningMinutes = Math.Max(1, await settings.GetAsync<int>(
            SettingKeys.Triggers.EscalationWarningMinutes, ct));
        var warnings = await repo.ListEscalationWarningCandidatesAsync(
            warningMinutes, CandidateLimit, ct);
        foreach (var c in warnings)
        {
            if (ct.IsCancellationRequested) return;
            await triggerService.EvaluateScheduledAsync(c.TriggerId, c.TicketId, c.BoundaryUtc, ct);
        }
    }
}
