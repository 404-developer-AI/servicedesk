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
/// <para>
/// v0.0.100 — reminders no longer re-evaluate forever: once a tick has
/// run every reminder trigger against an elapsed ticket and none applied,
/// the elapsed <c>pending_till_utc</c> is cleared (optimistically guarded
/// on the boundary so a re-arm wins). Before this a ticket whose reminder
/// could never match (e.g. closed via a path that leaked the timer) was
/// reloaded, evaluated and logged every single tick — 86K
/// <c>skipped_no_match</c> rows per ticket and 1.5 GB of trigger_runs on
/// one install. The same tick also runs the skipped-run retention sweep
/// (<c>Triggers.SkippedRunRetentionDays</c>).
/// </para>
public sealed class TriggerSchedulerWorker : BackgroundService
{
    private const int CandidateLimit = 500;

    // Retention sweep budget: 25 batches × 20K rows per pass. A pass
    // that hits the cap re-runs on the next tick (drains a multi-million
    // backlog in minutes without one long-lived DELETE); an idle pass
    // re-checks hourly.
    private const int SweepBatchSize = 20_000;
    private const int SweepMaxBatchesPerPass = 25;
    private static readonly TimeSpan SweepIdleInterval = TimeSpan.FromHours(1);
    internal DateTime NextSweepUtc { get; private set; } = DateTime.MinValue;

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

    private async Task TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<ITriggerRepository>();
        var triggerService = sp.GetRequiredService<ITriggerService>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var mutator = sp.GetRequiredService<SystemFieldMutator>();

        await RunRemindersAsync(repo, triggerService, mutator, ct);
        await RunEscalationsAsync(repo, triggerService, settings, ct);
        await SweepSkippedRunsAsync(repo, settings, ct);
    }

    /// Reminder candidates: pending-till elapsed. Candidates arrive ordered
    /// by (boundary, ticket) so one ticket's (ticket, trigger) pairs are
    /// adjacent; we track per ticket whether anything applied and clear
    /// the elapsed pending_till_utc when nothing did. The last ticket of a
    /// *full* batch may have been cut off by the LIMIT (its remaining
    /// pairs come next tick), so it is deliberately not cleared yet.
    /// Chained candidates keep their own clear path (pointer + boundary).
    private static async Task RunRemindersAsync(
        ITriggerRepository repo, ITriggerService triggerService, SystemFieldMutator mutator, CancellationToken ct)
    {
        var reminders = await repo.ListReminderCandidatesAsync(CandidateLimit, ct);
        if (reminders.Count == 0) return;

        var batchIsFull = reminders.Count >= CandidateLimit;
        var lastTicketId = reminders[^1].TicketId;

        var evaluated = new List<ReminderEvaluation>(reminders.Count);
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
            evaluated.Add(new ReminderEvaluation(c.TicketId, c.BoundaryUtc, result.Outcome));
        }

        foreach (var (ticketId, boundary) in ResolveElapsedClears(evaluated, batchIsFull, lastTicketId))
        {
            if (ct.IsCancellationRequested) return;
            await mutator.ClearElapsedReminderAsync(ticketId, boundary, ct);
        }
    }

    internal readonly record struct ReminderEvaluation(Guid TicketId, DateTime BoundaryUtc, TriggerRunOutcome? Outcome);

    /// Pure decision: which tickets get their elapsed pending_till_utc
    /// cleared after a reminder pass. A ticket is cleared when none of its
    /// evaluated pairs applied; the last ticket of a full batch is skipped
    /// (its remaining pairs may be in the next batch). Order-preserving.
    internal static IReadOnlyList<(Guid TicketId, DateTime BoundaryUtc)> ResolveElapsedClears(
        IReadOnlyList<ReminderEvaluation> evaluated, bool batchIsFull, Guid lastTicketId)
    {
        var order = new List<Guid>();
        var perTicket = new Dictionary<Guid, (DateTime Boundary, bool Applied)>();
        foreach (var e in evaluated)
        {
            var applied = e.Outcome == TriggerRunOutcome.Applied;
            if (perTicket.TryGetValue(e.TicketId, out var seen))
                perTicket[e.TicketId] = (seen.Boundary, seen.Applied || applied);
            else
            {
                perTicket[e.TicketId] = (e.BoundaryUtc, applied);
                order.Add(e.TicketId);
            }
        }

        var clears = new List<(Guid, DateTime)>();
        foreach (var ticketId in order)
        {
            var state = perTicket[ticketId];
            if (state.Applied) continue;
            if (batchIsFull && ticketId == lastTicketId) continue;
            clears.Add((ticketId, state.Boundary));
        }
        return clears;
    }

    private static async Task RunEscalationsAsync(
        ITriggerRepository repo, ITriggerService triggerService, ISettingsService settings, CancellationToken ct)
    {

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

    /// v0.0.100 — sweeps skipped_* run rows older than the retention
    /// window in bounded batches. Runs hourly when idle; a pass that
    /// exhausts its batch budget re-runs on the very next tick so a
    /// large backlog drains quickly without a single long DELETE.
    internal async Task SweepSkippedRunsAsync(ITriggerRepository repo, ISettingsService settings, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < NextSweepUtc) return;

        var days = Math.Clamp(await settings.GetAsync<int>(SettingKeys.Triggers.SkippedRunRetentionDays, ct), 0, 3650);
        if (days == 0)
        {
            NextSweepUtc = now + SweepIdleInterval;
            return;
        }

        var cutoff = now.AddDays(-days);
        var deleted = 0;
        var batches = 0;
        while (batches < SweepMaxBatchesPerPass && !ct.IsCancellationRequested)
        {
            var n = await repo.DeleteSkippedRunsOlderThanAsync(cutoff, SweepBatchSize, ct);
            deleted += n;
            batches++;
            if (n < SweepBatchSize) break;
        }

        var hitCap = batches >= SweepMaxBatchesPerPass && deleted >= SweepBatchSize * SweepMaxBatchesPerPass;
        NextSweepUtc = hitCap ? DateTime.MinValue : now + SweepIdleInterval;
        if (deleted > 0)
        {
            _logger.LogInformation(
                "Trigger run retention: swept {Deleted} skipped run rows older than {Days} days{More}.",
                deleted, days, hitCap ? " (backlog remains, continuing next tick)" : string.Empty);
        }
    }
}
