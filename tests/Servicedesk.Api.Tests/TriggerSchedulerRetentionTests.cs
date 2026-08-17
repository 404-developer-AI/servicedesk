using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers;
using Xunit;
using static Servicedesk.Infrastructure.Triggers.TriggerSchedulerWorker;

namespace Servicedesk.Api.Tests;

/// v0.0.100 — the scheduler no longer re-evaluates an elapsed reminder
/// forever. Two pure pieces are pinned here: the "which tickets get their
/// elapsed pending_till cleared" decision, and the bounded retention sweep
/// over skipped_* run rows. (The DB writes themselves are one-statement
/// UPDATE/DELETEs exercised by the integration deploy.)
public sealed class TriggerSchedulerRetentionTests
{
    private static readonly DateTime Boundary = new(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ticket_with_no_applied_run_is_cleared()
    {
        var t = Guid.NewGuid();
        var evaluated = new[]
        {
            new ReminderEvaluation(t, Boundary, TriggerRunOutcome.SkippedNoMatch),
            new ReminderEvaluation(t, Boundary, TriggerRunOutcome.SkippedNoMatch),
        };

        var clears = ResolveElapsedClears(evaluated, batchIsFull: false, lastTicketId: t);

        var only = Assert.Single(clears);
        Assert.Equal(t, only.TicketId);
        Assert.Equal(Boundary, only.BoundaryUtc);
    }

    [Fact]
    public void Ticket_where_any_trigger_applied_is_left_alone()
    {
        // One reminder trigger matched (and its actions may have re-armed
        // pending_till or flipped status); another skipped. The applied
        // path owns the ticket state — no wide clear.
        var t = Guid.NewGuid();
        var evaluated = new[]
        {
            new ReminderEvaluation(t, Boundary, TriggerRunOutcome.SkippedNoMatch),
            new ReminderEvaluation(t, Boundary, TriggerRunOutcome.Applied),
        };

        Assert.Empty(ResolveElapsedClears(evaluated, batchIsFull: false, lastTicketId: t));
    }

    [Fact]
    public void Failed_and_missing_outcomes_still_clear()
    {
        // Failed already dedups at the SQL layer; a null outcome means the
        // trigger/ticket vanished. Neither should keep the ticket a
        // candidate.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var evaluated = new[]
        {
            new ReminderEvaluation(a, Boundary, TriggerRunOutcome.Failed),
            new ReminderEvaluation(b, Boundary, null),
        };

        var clears = ResolveElapsedClears(evaluated, batchIsFull: false, lastTicketId: b);
        Assert.Equal(new[] { a, b }, clears.Select(c => c.TicketId));
    }

    [Fact]
    public void Last_ticket_of_a_full_batch_is_deferred()
    {
        // The LIMIT may have cut the remaining (ticket, trigger) pairs of
        // this ticket off into the next tick — do not clear until we have
        // seen them all. Every other ticket in the batch is complete.
        var a = Guid.NewGuid();
        var last = Guid.NewGuid();
        var evaluated = new[]
        {
            new ReminderEvaluation(a, Boundary, TriggerRunOutcome.SkippedNoMatch),
            new ReminderEvaluation(last, Boundary, TriggerRunOutcome.SkippedNoMatch),
        };

        var clears = ResolveElapsedClears(evaluated, batchIsFull: true, lastTicketId: last);
        var only = Assert.Single(clears);
        Assert.Equal(a, only.TicketId);

        // Same input, batch not full → both cleared.
        Assert.Equal(2, ResolveElapsedClears(evaluated, batchIsFull: false, lastTicketId: last).Count);
    }

    [Fact]
    public async Task Sweep_drains_backlog_in_batches_and_reschedules_immediately_when_capped()
    {
        // 25 batches × 20K per pass. A backlog of 600K rows needs 30
        // batches: pass 1 hits the cap and must schedule itself for the
        // next tick; pass 2 finishes and goes idle for an hour.
        var repo = new CountingRepo(rowsRemaining: 600_000);
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Triggers.SkippedRunRetentionDays, "30");
        var worker = new TriggerSchedulerWorker(new ServiceProviderStub(), NullLogger<TriggerSchedulerWorker>.Instance);

        await worker.SweepSkippedRunsAsync(repo, settings, CancellationToken.None);
        Assert.Equal(25, repo.Calls);
        Assert.Equal(500_000, repo.Deleted);
        Assert.Equal(DateTime.MinValue, worker.NextSweepUtc); // capped → next tick

        await worker.SweepSkippedRunsAsync(repo, settings, CancellationToken.None);
        Assert.Equal(600_000, repo.Deleted);
        Assert.True(worker.NextSweepUtc > DateTime.UtcNow.AddMinutes(50)); // idle → ~1h

        // Idle window respected: no further calls until it elapses.
        var callsBefore = repo.Calls;
        await worker.SweepSkippedRunsAsync(repo, settings, CancellationToken.None);
        Assert.Equal(callsBefore, repo.Calls);
    }

    [Fact]
    public async Task Sweep_is_disabled_at_zero_days()
    {
        var repo = new CountingRepo(rowsRemaining: 1000);
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Triggers.SkippedRunRetentionDays, "0");
        var worker = new TriggerSchedulerWorker(new ServiceProviderStub(), NullLogger<TriggerSchedulerWorker>.Instance);

        await worker.SweepSkippedRunsAsync(repo, settings, CancellationToken.None);

        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task Sweep_uses_the_retention_cutoff()
    {
        var repo = new CountingRepo(rowsRemaining: 10);
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Triggers.SkippedRunRetentionDays, "7");
        var worker = new TriggerSchedulerWorker(new ServiceProviderStub(), NullLogger<TriggerSchedulerWorker>.Instance);

        await worker.SweepSkippedRunsAsync(repo, settings, CancellationToken.None);

        var expected = DateTime.UtcNow.AddDays(-7);
        Assert.NotNull(repo.LastCutoff);
        Assert.InRange((repo.LastCutoff!.Value - expected).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class CountingRepo : ITriggerRepository
    {
        private int _remaining;
        public CountingRepo(int rowsRemaining) => _remaining = rowsRemaining;
        public int Calls { get; private set; }
        public int Deleted { get; private set; }
        public DateTime? LastCutoff { get; private set; }

        public Task<int> DeleteSkippedRunsOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
        {
            Calls++;
            LastCutoff = cutoffUtc;
            var n = Math.Min(batchSize, _remaining);
            _remaining -= n;
            Deleted += n;
            return Task.FromResult(n);
        }

        public Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(TriggerActivatorKind activatorKind, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task ReorderAsync(IReadOnlyList<TriggerPlacement> placements, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(DateTime sinceUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(Guid triggerId, int limit, DateTime? sinceUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(int warningMinutes, int limit, CancellationToken ct) => throw new NotImplementedException();
    }
}
